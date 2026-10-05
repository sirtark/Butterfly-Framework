using Butterfly.Chrysalis.Binary;
using Butterfly.Chrysalis.Grpc;
using Butterfly.Chrysalis.Http;
using Butterfly.Chrysalis.Rest;
using Butterfly.Chrysalis.Tests;
using Butterfly.Chrysalis.Tests.Inventory;
using Butterfly.Networking.Sockets;
using Butterfly.Serialization.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

// The tests count service scopes with a static counter: they must not run at the same time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Butterfly.Chrysalis.AspNetCore.Tests
{
    // Counts the scopes it is created in: proves implementations are resolved per call from dependency injection.
    public sealed class ScopedCounter
    {
        private static int created;
        public ScopedCounter() => Interlocked.Increment(ref created);
        public static int Created => Volatile.Read(ref created);
    }

    public sealed class ScopedInventory(ScopedCounter counter) : IInventoryService
    {
        private static readonly InventoryService Shared = new();
        public ScopedCounter Counter { get; } = counter;

        public Task<Product> GetProduct(int id) => Shared.GetProduct(id);
        public Task<IReadOnlyList<Product>> Search(string? text, Category? category, CancellationToken cancellationToken) => Shared.Search(text, category, cancellationToken);
        public Task<Product> AddProduct(Product product) => Shared.AddProduct(product);
        public int AddStock(int productId, int quantity) => Shared.AddStock(productId, quantity);
        public Task Delete(int id) => Shared.Delete(id);
        public Task<IReadOnlyList<Product>> Find(ProductFilter filter, int? limit) => Shared.Find(filter, limit);
        public Sample Echo(Sample sample) => sample;
        public void Fail(ChrysalisStatus status, string message) => Shared.Fail(status, message);
        public Task<string> Slow(int milliseconds, CancellationToken cancellationToken) => Shared.Slow(milliseconds, cancellationToken);
        public string Identify(ChrysalisCallContext context) => Shared.Identify(context);
    }

    // Middleware from dependency injection, with a scoped dependency.
    public sealed class AuditMiddleware(ScopedCounter counter) : IChrysalisMiddleware
    {
        public static readonly List<string> Log = [];

        public async ValueTask<object?> InvokeAsync(ChrysalisCallContext context, ChrysalisNext next)
        {
            lock (Log)
                Log.Add($"{context.Protocol}:{context.Operation.Name}:{counter.GetHashCode() != 0}");
            return await next(context);
        }
    }

    public class AspNetCoreTests : IAsyncLifetime
    {
        private WebApplication app = null!;
        private Uri http1 = null!;
        private Uri http2 = null!;

        public async Task InitializeAsync()
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.ConfigureKestrel(kestrel =>
            {
                kestrel.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http1);
                // Cleartext HTTP/2 (h2c) for gRPC: no TLS, so no ALPN to negotiate it.
                kestrel.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http2);
            });
            builder.Services.AddScoped<ScopedCounter>();
            builder.Services.AddChrysalis(chrysalis => chrysalis
                .Expose<IInventoryService, ScopedInventory>()
                .Use<AuditMiddleware>());

            app = builder.Build();
            // A stand-in for authentication: the X-User header becomes HttpContext.User.
            app.Use((context, next) =>
            {
                if (context.Request.Headers["X-User"] is [{ } user])
                    context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, user)], "header"));
                return next(context);
            });
            app.UseChrysalisGrpc();
            app.MapChrysalisRest("/api");
            app.MapChrysalisSoap("/soap");
            app.MapChrysalisJsonRpc("/rpc");
            app.MapChrysalisXmlRpc("/xmlrpc");
            app.MapGet("/plain", () => "not chrysalis");
            await app.StartAsync();

            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Select(address => new Uri(address)).ToList();
            (http1, http2) = (addresses[0], addresses[1]);
        }

        public async Task DisposeAsync() => await app.DisposeAsync();

        private HttpClient Client() => new() { BaseAddress = http1 };

        [Fact]
        public async Task ServesRestFromKestrel()
        {
            using var client = Client();

            var product = await client.GetStringAsync("/api/inventory/products/1");
            var missing = await client.GetAsync("/api/inventory/products/404");

            Assert.Contains("\"name\":\"Dune\"", product);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            Assert.Equal("application/problem+json", missing.Content.Headers.ContentType!.MediaType);
            Assert.Equal("not chrysalis", await client.GetStringAsync("/plain"));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ServesQueryFromKestrel(bool overHttp2)
        {
            // Kestrel passes the QUERY method (RFC 10008) and its body through, on HTTP/1.1 and on HTTP/2.
            using var client = new HttpClient { BaseAddress = overHttp2 ? http2 : http1 };
            HttpRequestMessage Request(HttpMethod method, string path, string? json) => new(method, path)
            {
                Content = json is null ? null : new StringContent(json, Encoding.UTF8, "application/json"),
                Version = overHttp2 ? HttpVersion.Version20 : HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact
            };

            using var found = await client.SendAsync(Request(new HttpMethod("QUERY"), "/api/inventory/search?limit=1", """{"categories":["Music","Games"]}"""));
            using var get = await client.SendAsync(Request(HttpMethod.Get, "/api/inventory/search", null));

            Assert.Equal(overHttp2 ? HttpVersion.Version20 : HttpVersion.Version11, found.Version);
            Assert.Equal(HttpStatusCode.OK, found.StatusCode);
            Assert.Equal(2, JsonDocument.Parse(await found.Content.ReadAsStringAsync()).RootElement.EnumerateArray().Single().GetProperty("id").GetInt32());
            Assert.Equal(["application/json"], found.Headers.GetValues("Accept-Query"));
            Assert.Equal(HttpStatusCode.MethodNotAllowed, get.StatusCode);
            Assert.Equal(["QUERY"], get.Content.Headers.Allow);
        }

        [Fact]
        public async Task ServesJsonRpcXmlRpcAndSoapFromKestrel()
        {
            using var client = Client();

            var jsonRpc = await client.PostAsync("/rpc", new StringContent("""{"jsonrpc":"2.0","method":"InventoryService.GetProduct","params":[2],"id":1}""", Encoding.UTF8, "application/json"));
            var xmlRpc = await client.PostAsync("/xmlrpc", new StringContent("<methodCall><methodName>system.listMethods</methodName></methodCall>", Encoding.UTF8, "text/xml"));
            var wsdl = XDocument.Parse(await client.GetStringAsync("/soap/InventoryService?wsdl"));

            Assert.Equal("Kind of Blue", JsonDocument.Parse(await jsonRpc.Content.ReadAsStringAsync()).RootElement.GetProperty("result").GetProperty("name").GetString());
            Assert.Contains("InventoryService.Echo", await xmlRpc.Content.ReadAsStringAsync());
            Assert.Contains($"{http1.Authority}/soap/InventoryService", wsdl.ToString());
        }

        [Fact]
        public async Task AuthenticatedUsersAndScopesReachTheCall()
        {
            using var client = Client();
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/inventory/WhoAmI") { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            request.Headers.Add("X-User", "ada");
            var before = ScopedCounter.Created;

            var response = await client.SendAsync(request);

            Assert.Equal("\"REST:ada\"", await response.Content.ReadAsStringAsync());
            // One request scope: the implementation and the middleware shared one ScopedCounter.
            Assert.Equal(before + 1, ScopedCounter.Created);
            lock (AuditMiddleware.Log)
                Assert.Contains("REST:WhoAmI:True", AuditMiddleware.Log);
        }

        [Fact]
        public async Task ServesGrpcToTheOfficialClient()
        {
            using var channel = GrpcChannel.ForAddress(http2, new GrpcChannelOptions { HttpHandler = new SocketsHttpHandler() });
            var operation = ChrysalisRegistry.GetService<IInventoryService>().FindOperation("AddStock")!;
            var method = new Method<byte[], byte[]>(MethodType.Unary, GrpcMessages.ServiceName(operation.Service), "AddStock",
                Marshallers.Create(bytes => bytes, bytes => bytes), Marshallers.Create(bytes => bytes, bytes => bytes));

            var response = await channel.CreateCallInvoker().AsyncUnaryCall(method, null, default,
                ProtobufFormat.Instance.Serialize(operation.ParametersType, new object?[] { 9001, 4 })).ResponseAsync;
            var missing = new Method<byte[], byte[]>(MethodType.Unary, "inventory.v1.InventoryService", "Teleport", method.RequestMarshaller, method.ResponseMarshaller);
            var unimplemented = await Assert.ThrowsAsync<RpcException>(() => channel.CreateCallInvoker().AsyncUnaryCall(missing, null, default, []).ResponseAsync);

            Assert.Equal(4, ((object?[])ProtobufFormat.Instance.Deserialize(operation.ResultType, response)!)[0]);
            Assert.Equal(StatusCode.Unimplemented, unimplemented.StatusCode);
        }

        [Fact]
        public async Task MappedEndpointsAcceptAspNetCoreConventions()
        {
            // RequireAuthorization, CORS, rate limiting... apply to what the Map methods return.
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
            builder.Services.AddChrysalis(chrysalis => chrysalis.Expose<IInventoryService>(new InventoryService()));
            builder.Services.AddAuthentication("none").AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, NoAuthentication>("none", null);
            builder.Services.AddAuthorization();
            await using var secured = builder.Build();
            secured.UseAuthentication();
            secured.UseAuthorization();
            secured.MapChrysalisRest("/api").RequireAuthorization();
            await secured.StartAsync();
            var address = secured.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();

            using var client = new HttpClient { BaseAddress = new Uri(address) };
            var response = await client.GetAsync("/api/inventory/products/1");

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    // An authentication scheme that never authenticates anyone.
    public sealed class NoAuthentication(Microsoft.Extensions.Options.IOptionsMonitor<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions> options,
        Microsoft.Extensions.Logging.ILoggerFactory logger, System.Text.Encodings.Web.UrlEncoder encoder)
        : Microsoft.AspNetCore.Authentication.AuthenticationHandler<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<Microsoft.AspNetCore.Authentication.AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(Microsoft.AspNetCore.Authentication.AuthenticateResult.NoResult());
    }

    public class HostedServerTests
    {
        [Fact]
        public async Task RunsTheChrysalisServersAsHostedServices()
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Services.AddScoped<ScopedCounter>();
            builder.Services.AddChrysalis(chrysalis => chrysalis.Expose<IInventoryService, ScopedInventory>());
            builder.Services.AddChrysalisHttpServer(options => options.Endpoints.Add(HttpEndpoint.Loopback(0)), (http, chrysalis) => http.MapRest(chrysalis));
            builder.Services.AddChrysalisBinaryServer(options => options.Endpoints.Add((Butterfly.Networking.Sockets.SocketAddress.Loopback(Butterfly.Networking.Sockets.AddressFamily.IPv4, 0), null)));

            using var host = builder.Build();
            await host.StartAsync();
            try
            {
                var servers = host.Services.GetServices<IHostedService>().OfType<ChrysalisHostedService>().Select(service => service.Server).ToList();
                var http = servers.OfType<HttpServer>().Single();
                var binary = servers.OfType<ChrysalisBinaryServer>().Single();
                var before = ScopedCounter.Created;

                using var client = new HttpClient { BaseAddress = http.BaseAddress() };
                Assert.Contains("Dune", await client.GetStringAsync("/api/inventory/products/1"));
                await using var connection = await ChrysalisBinaryClient.ConnectAsync("127.0.0.1", binary.Endpoints[0].Port);
                Assert.Equal("Binary:anonymous", connection.CreateClient<IInventoryService>().Identify(null!));

                // Outside ASP.NET Core every call still gets its own scope.
                Assert.Equal(before + 2, ScopedCounter.Created);
            }
            finally
            {
                await host.StopAsync();
            }
        }
    }
}

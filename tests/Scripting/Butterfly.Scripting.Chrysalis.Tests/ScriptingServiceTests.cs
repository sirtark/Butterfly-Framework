using Butterfly.Chrysalis;
using Butterfly.Chrysalis.Binary;
using Butterfly.Chrysalis.Http;
using Butterfly.Chrysalis.Rest;
using Butterfly.Networking.Sockets;
using Butterfly.Scripting.CSharp;
using Microsoft.Extensions.DependencyInjection;
using System.Text;
using System.Text.Json;

namespace Butterfly.Scripting.Chrysalis.Tests
{
    public class ScriptingServiceTests : IAsyncLifetime
    {
        private ServiceProvider services = null!;
        private ChrysalisBinaryServer binary = null!;
        private HttpServer http = null!;

        public Task InitializeAsync()
        {
            services = new ServiceCollection().AddCSharpScriptInvoker().BuildServiceProvider();
            var chrysalis = new ChrysalisServer().Expose<IScriptingService>(new ScriptingService(services.GetRequiredService<IScriptInvoker>(), services));

            var options = new ChrysalisBinaryServerOptions();
            options.Endpoints.Add((SocketAddress.Loopback(AddressFamily.IPv4, 0), null));
            binary = new ChrysalisBinaryServer(chrysalis, options);
            binary.Start();

            var httpOptions = new HttpServerOptions();
            httpOptions.Endpoints.Add(HttpEndpoint.Loopback(0));
            http = new HttpServer(httpOptions).MapRest(chrysalis);
            http.Start();
            return Task.CompletedTask;
        }

        public async Task DisposeAsync()
        {
            await binary.StopAsync();
            await http.StopAsync();
            await services.DisposeAsync();
        }

        [Fact]
        public async Task RunsScriptsOverTheBinaryProtocol()
        {
            await using var connection = await ChrysalisBinaryClient.ConnectAsync("127.0.0.1", binary.Endpoints[0].Port);
            var scripting = connection.CreateClient<IScriptingService>();

            var sum = await scripting.Execute(new ScriptRequest(ScriptingLanguage.CSharp, "return 40 + 2;"), CancellationToken.None);
            var text = await scripting.Execute(new ScriptRequest(ScriptingLanguage.CSharp, "return \"hola \" + Parameters[\"name\"];",
                new Dictionary<string, object?> { ["name"] = "mundo" }), CancellationToken.None);

            // Results are dynamic: integers arrive as long.
            Assert.Equal(42L, sum);
            Assert.Equal("hola mundo", text);
        }

        [Fact]
        public async Task RunsScriptsOverRest()
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{http.Endpoints[0].Address.Port}/") };

            var response = await client.PostAsync("/api/scripts/execute",
                new StringContent("""{"language":"CSharp","source":"return new[] { 1, 2, 3 };"}""", Encoding.UTF8, "application/json"));

            Assert.Equal("[1,2,3]", await response.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task ScriptErrorsBecomeStatuses()
        {
            await using var connection = await ChrysalisBinaryClient.ConnectAsync("127.0.0.1", binary.Endpoints[0].Port);
            var scripting = connection.CreateClient<IScriptingService>();

            var compilation = await Assert.ThrowsAsync<ChrysalisException>(() => scripting.Execute(new ScriptRequest(ScriptingLanguage.CSharp, "return ;;; nope"), CancellationToken.None));
            var language = await Assert.ThrowsAsync<ChrysalisException>(() => scripting.Execute(new ScriptRequest(ScriptingLanguage.Python, "1"), CancellationToken.None));
            var empty = await Assert.ThrowsAsync<ChrysalisException>(() => scripting.Execute(new ScriptRequest(ScriptingLanguage.CSharp, " "), CancellationToken.None));

            Assert.Equal(ChrysalisStatus.InvalidArgument, compilation.Status);
            Assert.Equal(ChrysalisStatus.Unimplemented, language.Status);
            Assert.Equal(ChrysalisStatus.InvalidArgument, empty.Status);
        }
    }
}

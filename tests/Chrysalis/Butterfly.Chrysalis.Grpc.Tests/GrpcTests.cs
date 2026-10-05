using Butterfly.Serialization;
using Butterfly.Chrysalis.Http;
using Butterfly.Serialization.Protobuf;
using Butterfly.Chrysalis.Tests;
using Butterfly.Chrysalis.Tests.Inventory;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;

namespace Butterfly.Chrysalis.Grpc.Tests
{
    // Calls go through Grpc.Net.Client, Google's gRPC client for .NET, with raw-byte marshallers.
    public class GrpcTests : IAsyncLifetime
    {
        private const string ServiceName = "inventory.v1.InventoryService";
        private static readonly ChrysalisService Service = ChrysalisRegistry.GetService<IInventoryService>();

        private HttpServer http = null!;
        private ChrysalisServer chrysalis = null!;
        private GrpcChannel channel = null!;

        public Task InitializeAsync()
        {
            (http, chrysalis) = TestServers.StartChrysalis((http, chrysalis) => http.MapGrpc(chrysalis).Map("/", context =>
            {
                context.Response.Write("not grpc", "text/plain");
                return default;
            }));
            channel = GrpcChannel.ForAddress($"http://127.0.0.1:{http.Port()}", new GrpcChannelOptions { HttpHandler = new SocketsHttpHandler() });
            return Task.CompletedTask;
        }

        public async Task DisposeAsync()
        {
            channel.Dispose();
            await http.StopAsync();
        }

        private static Method<byte[], byte[]> Method(string name) =>
            new(MethodType.Unary, ServiceName, name, Marshallers.Create(bytes => bytes, bytes => bytes), Marshallers.Create(bytes => bytes, bytes => bytes));

        private Task<byte[]> Call(string operation, byte[] request, CallOptions options = default) =>
            channel.CreateCallInvoker().AsyncUnaryCall(Method(operation), null, options, request).ResponseAsync;

        // Encodes and decodes with Chrysalis, following the gRPC shapes of the operation.
        private async Task<object?> CallChrysalis(string operation, params object?[] arguments)
        {
            var op = Service.FindOperation(operation)!;
            var request = GrpcMessages.IsBareRequest(op) ? arguments[0]! : arguments;
            var response = await Call(operation, ProtobufFormat.Instance.Serialize(GrpcMessages.RequestType(op), request));
            var decoded = ProtobufFormat.Instance.Deserialize(GrpcMessages.ResponseType(op), response);
            return op.ReturnType is ObjectType ? decoded : op.ReturnType is null ? null : ((object?[])decoded!)[0];
        }

        [Fact]
        public async Task InteroperatesWithGoogleProtobufAndTheOfficialClient()
        {
            // GetProductRequest { int32 id = 1; } written by Google.Protobuf.
            var request = new MemoryStream();
            var output = new CodedOutputStream(request);
            output.WriteTag(1, WireFormat.WireType.Varint);
            output.WriteInt32(1);
            output.Flush();

            var response = await Call("GetProduct", request.ToArray());

            // Product read back field by field with Google.Protobuf.
            var input = new CodedInputStream(response);
            var fields = new Dictionary<int, object>();
            var tags = new List<string>();
            uint tag;
            while ((tag = input.ReadTag()) != 0)
            {
                var number = WireFormat.GetTagFieldNumber(tag);
                switch (number)
                {
                    case 1: fields[1] = input.ReadInt32(); break;
                    case 2: fields[2] = input.ReadString(); break;
                    case 3: fields[3] = input.ReadString(); break;
                    case 4: fields[4] = input.ReadEnum(); break;
                    case 5: tags.Add(input.ReadString()); break;
                    case 6:
                        var timestamp = new Google.Protobuf.WellKnownTypes.Timestamp();
                        input.ReadMessage(timestamp);
                        fields[6] = timestamp.ToDateTimeOffset();
                        break;
                    default: input.SkipLastField(); break;
                }
            }

            Assert.Equal(1, fields[1]);
            Assert.Equal("Dune", fields[2]);
            Assert.Equal("9.99", fields[3]);
            Assert.False(fields.ContainsKey(4)); // Books = 0, the default, is not sent
            Assert.Equal(["sci-fi", "classic"], tags);
            Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), fields[6]);
        }

        [Fact]
        public async Task CallsEveryShapeOfOperation()
        {
            var added = (Product)(await CallChrysalis("AddProduct", new Product(30, "Snow Crash", 7m, Category.Books, ["cyberpunk"])))!;
            Assert.Equal("Snow Crash", added.Name);

            Assert.Equal(5, await CallChrysalis("AddStock", 30, 5));
            Assert.Null(await CallChrysalis("Delete", 30));
            Assert.Equal(3, ((IReadOnlyList<Product>)(await CallChrysalis("Search", null, null))!).Count);

            var sample = Samples.Full();
            Samples.AssertEqual(sample, (Sample)(await CallChrysalis("Echo", sample))!);
        }

        [Fact]
        public async Task ErrorsBecomeGrpcStatuses()
        {
            var notFound = await Assert.ThrowsAsync<RpcException>(() => CallChrysalis("GetProduct", 99));
            Assert.Equal(StatusCode.NotFound, notFound.StatusCode);
            Assert.Equal("Product 99 does not exist.", notFound.Status.Detail);

            var unimplemented = await Assert.ThrowsAsync<RpcException>(() => Call("Teleport", []));
            Assert.Equal(StatusCode.Unimplemented, unimplemented.StatusCode);

            var internalError = await Assert.ThrowsAsync<RpcException>(() => CallChrysalis("Fail", ChrysalisStatus.Internal, "secret"));
            Assert.Equal(StatusCode.Internal, internalError.StatusCode);
            Assert.DoesNotContain("secret", internalError.Status.Detail);

            // Non-ASCII messages survive the percent-encoding of grpc-message.
            var accented = await Assert.ThrowsAsync<RpcException>(() => CallChrysalis("Fail", ChrysalisStatus.FailedPrecondition, "Stock agotado: 100% vendido ñ"));
            Assert.Equal("Stock agotado: 100% vendido ñ", accented.Status.Detail);
        }

        [Fact]
        public async Task MalformedMessagesAreInvalidArguments()
        {
            var exception = await Assert.ThrowsAsync<RpcException>(() => Call("GetProduct", [0x08]));

            Assert.Equal(StatusCode.InvalidArgument, exception.StatusCode);
        }

        [Fact]
        public async Task MetadataReachesMiddleware()
        {
            chrysalis.Use((context, next) =>
            {
                if (context.Headers.TryGetValue("x-user", out var user))
                    context.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([new(System.Security.Claims.ClaimTypes.Name, user)], "test"));
                return next(context);
            });

            var response = await Call("WhoAmI", [], new CallOptions(new Metadata { { "x-user", "ada" } }));

            Assert.Equal("gRPC:ada", ((object?[])ProtobufFormat.Instance.Deserialize(Service.FindOperation("WhoAmI")!.ResultType, response)!)[0]);
        }

        [Fact]
        public async Task AcceptsGzipCompressedRequests()
        {
            var op = Service.FindOperation("Echo")!;
            var sample = new Sample { Text = new string('z', 10_000) };

            // Grpc.Net.Client compresses the request when this metadata entry is present.
            var response = await Call("Echo", ProtobufFormat.Instance.Serialize(GrpcMessages.RequestType(op), sample),
                new CallOptions(new Metadata { { "grpc-internal-encoding-request", "gzip" } }));

            Assert.Equal(sample.Text, ((Sample)ProtobufFormat.Instance.Deserialize(GrpcMessages.ResponseType(op), response)!).Text);
        }

        [Fact]
        public async Task ServerDeadlinesCancelTheCall()
        {
            // Raw HTTP/2 request: the official client would enforce the deadline itself and hide the server's answer.
            using var client = TestServers.Client(http, http2: true);
            var op = Service.FindOperation("Slow")!;
            var message = ProtobufFormat.Instance.Serialize(op.ParametersType, new object?[] { 10_000 });
            var frame = new byte[5 + message.Length];
            BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(1), (uint)message.Length);
            message.CopyTo(frame, 5);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/{ServiceName}/Slow")
            {
                Content = new ByteArrayContent(frame),
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
            request.Headers.Add("grpc-timeout", "200m");
            request.Headers.TE.Add(new TransferCodingWithQualityHeaderValue("trailers"));

            var watch = Stopwatch.StartNew();
            using var response = await client.SendAsync(request);
            await response.Content.ReadAsByteArrayAsync();

            Assert.Equal("4", response.Headers.GetValues("grpc-status").Single()); // DEADLINE_EXCEEDED, trailers-only
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
        }

        [Fact]
        public async Task GrpcNeedsHttp2()
        {
            using var client = TestServers.Client(http);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/{ServiceName}/GetProduct") { Content = new ByteArrayContent([0, 0, 0, 0, 0]) };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");

            Assert.Equal(HttpStatusCode.HttpVersionNotSupported, (await client.SendAsync(request)).StatusCode);
            Assert.Equal("not grpc", await client.GetStringAsync("/anything")); // other paths still work
        }

        [Fact]
        public async Task ManyConcurrentCallsShareTheChannel()
        {
            var results = await Task.WhenAll(Enumerable.Range(1, 50).Select(i => CallChrysalis("AddStock", 1000 + i, i)));

            Assert.Equal(Enumerable.Range(1, 50).Select(i => (object?)i), results);
        }

        [Fact]
        public void GeneratesAProtoFile()
        {
            var proto = ProtoFile.Generate(Service);

            Assert.Contains("syntax = \"proto3\";", proto);
            Assert.Contains("package inventory.v1;", proto);
            Assert.Contains("import \"google/protobuf/timestamp.proto\";", proto);
            Assert.Contains("rpc GetProduct (GetProductRequest) returns (Product);", proto);
            Assert.Contains("rpc AddProduct (Product) returns (Product);", proto);
            Assert.Contains("rpc Delete (DeleteRequest) returns (DeleteResponse);", proto);
            Assert.Contains("message GetProductRequest {\n  int32 id = 1;\n}".ReplaceLineEndings(), proto);
            Assert.Contains("  repeated string tags = 5;", proto);
            Assert.Contains("  optional google.protobuf.Timestamp created_at = 6;", proto);
            Assert.Contains("  CATEGORY_GAMES = 10;", proto);
            Assert.Contains("  repeated Sample children = 17;", proto);
        }

        [Fact]
        public async Task TheProtoFileCompilesWithProtoc()
        {
            // protoc ships in the Grpc.Tools package; the check runs where the package is in the NuGet cache.
            var cache = Environment.GetEnvironmentVariable("NUGET_PACKAGES") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
            var platform = OperatingSystem.IsWindows() ? "windows_x64" : OperatingSystem.IsMacOS() ? "macosx_x64" : "linux_x64";
            var tools = Directory.Exists(Path.Combine(cache, "grpc.tools")) ? Directory.GetDirectories(Path.Combine(cache, "grpc.tools")).OrderDescending().FirstOrDefault() : null;
            var protoc = tools is null ? null : Path.Combine(tools, "tools", platform, OperatingSystem.IsWindows() ? "protoc.exe" : "protoc");
            if (protoc is null || !File.Exists(protoc))
                return;

            var directory = Directory.CreateTempSubdirectory("chrysalis-proto").FullName;
            try
            {
                await File.WriteAllTextAsync(Path.Combine(directory, "inventory.proto"), ProtoFile.Generate(Service));
                var start = new ProcessStartInfo(protoc) { RedirectStandardError = true, RedirectStandardOutput = true, WorkingDirectory = directory };
                foreach (var argument in new[] { $"--proto_path={directory}", $"--proto_path={Path.Combine(tools!, "build", "native", "include")}", $"--descriptor_set_out={Path.Combine(directory, "out.pb")}", "inventory.proto" })
                    start.ArgumentList.Add(argument);

                using var process = Process.Start(start)!;
                var errors = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();

                Assert.True(process.ExitCode == 0, errors);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}

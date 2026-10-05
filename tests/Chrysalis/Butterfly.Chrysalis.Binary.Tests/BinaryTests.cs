using Butterfly.Chrysalis.Tests;
using Butterfly.Chrysalis.Tests.Inventory;
using Butterfly.Communication;
using Butterfly.Networking.Sockets;
using System.Net.Sockets;
using System.Security.Claims;

namespace Butterfly.Chrysalis.Binary.Tests
{
    public class BinaryTests : IAsyncLifetime
    {
        private ChrysalisServer chrysalis = null!;
        private ChrysalisBinaryServer server = null!;
        private ChrysalisBinaryServer tlsServer = null!;
        private readonly TaskCompletionSource slowCancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int Port => server.Endpoints[0].Port;

        public Task InitializeAsync()
        {
            chrysalis = new ChrysalisServer().Expose<IInventoryService>(new InventoryService());
            chrysalis.Use(async (context, next) =>
            {
                if (context.Headers.TryGetValue("user", out var user))
                    context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, user)], "test"));
                try
                {
                    return await next(context);
                }
                catch (ChrysalisException exception) when (context.Operation.Name == "Slow" && exception.Status == ChrysalisStatus.Cancelled)
                {
                    slowCancelled.TrySetResult();
                    throw;
                }
            });

            server = Start(certificate: null);
            tlsServer = Start(TestCertificate.Value);
            return Task.CompletedTask;
        }

        private ChrysalisBinaryServer Start(System.Security.Cryptography.X509Certificates.X509Certificate2? certificate)
        {
            var options = new ChrysalisBinaryServerOptions { MaxConcurrentCallsPerConnection = 64 };
            options.Endpoints.Add((SocketAddress.Loopback(Networking.Sockets.AddressFamily.IPv4, 0), certificate));
            var started = new ChrysalisBinaryServer(chrysalis, options);
            started.Start();
            return started;
        }

        public async Task DisposeAsync()
        {
            await server.StopAsync();
            await tlsServer.StopAsync();
        }

        [Fact]
        public async Task TheGeneratedClientWorksOverTheBinaryProtocol()
        {
            await using var connection = await ChrysalisBinaryClient.ConnectAsync("127.0.0.1", Port);
            var inventory = connection.CreateClient<IInventoryService>();

            Assert.Equal("Dune", (await inventory.GetProduct(1)).Name);
            Assert.Equal(2, (await inventory.Search("o", null, CancellationToken.None)).Count);
            Assert.Equal(3, inventory.AddStock(1, 3));
            await inventory.Delete(3);
            Samples.AssertEqual(Samples.Full(), inventory.Echo(Samples.Full()));

            var added = await inventory.AddProduct(new Product(40, "Hyperion", 11m, Category.Books, ["sci-fi"]));
            Assert.Equal(added with { Tags = [] }, (await inventory.GetProduct(40)) with { Tags = [] });
        }

        [Fact]
        public async Task ErrorsKeepTheirStatus()
        {
            await using var connection = await ChrysalisBinaryClient.ConnectAsync("127.0.0.1", Port);
            var inventory = connection.CreateClient<IInventoryService>();

            var notFound = await Assert.ThrowsAsync<ChrysalisException>(() => inventory.GetProduct(99));
            Assert.Equal(ChrysalisStatus.NotFound, notFound.Status);
            Assert.Equal("Product 99 does not exist.", notFound.Message);

            var hidden = Assert.Throws<ChrysalisException>(() => inventory.Fail(ChrysalisStatus.Internal, "secret"));
            Assert.Equal("An internal error occurred.", hidden.Message);
        }

        [Fact]
        public async Task MetadataTravelsWithEveryCall()
        {
            await using var connection = await ChrysalisBinaryClient.ConnectAsync("127.0.0.1", Port);
            connection.Metadata["user"] = "grace";

            Assert.Equal("Binary:grace", connection.CreateClient<IInventoryService>().Identify(null!));
        }

        [Fact]
        public async Task CancellingACallCancelsItOnTheServer()
        {
            await using var connection = await ChrysalisBinaryClient.ConnectAsync("127.0.0.1", Port);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

            var exception = await Assert.ThrowsAsync<ChrysalisException>(() => connection.CreateClient<IInventoryService>().Slow(30_000, cancellation.Token));

            Assert.Equal(ChrysalisStatus.Cancelled, exception.Status);
            await slowCancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, connection.CreateClient<IInventoryService>().AddStock(77, 1)); // the connection is still usable
        }

        [Fact]
        public async Task CallsAreMultiplexed()
        {
            await using var connection = await ChrysalisBinaryClient.ConnectAsync("127.0.0.1", Port);
            var inventory = connection.CreateClient<IInventoryService>();

            // A slow call (10 min, cancelled at the end) does not block the others behind it on the same connection.
            // The budget is generous: 40 blocking calls can take a while when the thread pool is starved by a full test run.
            using var cancellation = new CancellationTokenSource();
            var slow = inventory.Slow(600_000, cancellation.Token);
            var quick = await Task.WhenAll(Enumerable.Range(1, 40).Select(i => Task.Run(() => inventory.AddStock(500 + i, i))));

            Assert.False(slow.IsCompleted);
            Assert.Equal(Enumerable.Range(1, 40), quick);
            cancellation.Cancel();
            Assert.Equal(ChrysalisStatus.Cancelled, (await Assert.ThrowsAsync<ChrysalisException>(() => slow)).Status);
        }

        [Fact]
        public async Task PingsMeasureTheRoundTrip()
        {
            await using var connection = await ChrysalisBinaryClient.ConnectAsync("127.0.0.1", Port);

            Assert.True(await connection.PingAsync() < TimeSpan.FromSeconds(5));
        }

        [Fact]
        public async Task WorksOverTls()
        {
            var options = new ConnectionOptions { ReadTimeout = Timeout.InfiniteTimeSpan, Tls = TlsOptions.InsecureAcceptAnyCertificate };
            await using var connection = await ChrysalisBinaryClient.ConnectAsync("localhost", tlsServer.Endpoints[0].Port, useTls: true, options);

            Assert.Equal("Dune", (await connection.CreateClient<IInventoryService>().GetProduct(1)).Name);
        }

        [Fact]
        public async Task ClosesConnectionsThatDoNotSpeakTheProtocol()
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync("127.0.0.1", Port);
            var stream = tcp.GetStream();
            await stream.WriteAsync("GET / HTTP/1.1\r\n\r\n"u8.ToArray());

            // The server closes without answering: a clean end of stream, or a reset when our request is still unread.
            try
            {
                Assert.Equal(0, await stream.ReadAsync(new byte[16]).AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
            }
            catch (IOException exception) when (exception.InnerException is System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.ConnectionReset })
            {
            }
        }

        [Fact]
        public async Task ClientsLearnWhenTheServerGoesAway()
        {
            var options = new ChrysalisBinaryServerOptions();
            options.Endpoints.Add((SocketAddress.Loopback(Networking.Sockets.AddressFamily.IPv4, 0), null));
            var temporary = new ChrysalisBinaryServer(chrysalis, options);
            temporary.Start();
            await using var connection = await ChrysalisBinaryClient.ConnectAsync("127.0.0.1", temporary.Endpoints[0].Port);
            var inventory = connection.CreateClient<IInventoryService>();
            Assert.Equal(1, inventory.AddStock(900, 1));

            await temporary.StopAsync();

            var exception = await Assert.ThrowsAsync<ChrysalisException>(async () =>
            {
                // The client notices the closed connection on its next read or write.
                for (var i = 0; i < 50; i++)
                {
                    inventory.AddStock(900, 1);
                    await Task.Delay(50);
                }
            });
            Assert.Equal(ChrysalisStatus.Unavailable, exception.Status);
            Assert.False(connection.IsConnected);
        }

        [Fact]
        public async Task UnknownOperationsAreUnimplemented()
        {
            await using var connection = await ChrysalisBinaryClient.ConnectAsync("127.0.0.1", Port);
            var foreign = new ChrysalisOperation("Teleport", [], null, false, (_, _, _) => default);
            _ = new ChrysalisService("Elsewhere", "x", typeof(object), null, [foreign]);

            var exception = await Assert.ThrowsAsync<ChrysalisException>(() => connection.InvokeAsync(foreign, [], CancellationToken.None).AsTask());

            Assert.Equal(ChrysalisStatus.Unimplemented, exception.Status);
        }
    }
}

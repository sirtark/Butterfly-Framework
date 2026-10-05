using System.Diagnostics;
using System.Security.Authentication;

using Butterfly.Communication.Testing;
using Butterfly.Networking.Sockets;

namespace Butterfly.Communication.Tests
{
    public class NetworkConnectionTests
    {
        [Fact]
        public void ConnectsByNameAndExchangesLines()
        {
            using var server = new TestServer(session =>
            {
                session.WriteLine("220 hola");
                session.Expect("PING");
                session.WriteLine("PONG");
            });

            using var connection = NetworkConnection.Connect("localhost", server.Port);
            Assert.Equal("220 hola", connection.Reader.ReadLine());
            connection.WriteLine("PING");
            Assert.Equal("PONG", connection.Reader.ReadLine());
            Assert.False(connection.IsSecure);

            server.Wait();
        }

        [Fact]
        public void ReportsRefusedConnections()
        {
            ushort port;
            using (var probe = new TcpSocket())
            {
                probe.Bind(SocketAddress.Loopback(AddressFamily.IPv4, 0));
                port = probe.LocalAddress.Port;
            }

            var exception = Assert.Throws<ConnectionFailedException>(() => NetworkConnection.Connect("127.0.0.1", port));
            Assert.Equal(port, exception.Port);
            Assert.IsType<SocketException>(exception.InnerException);
        }

        [Fact]
        public void ImplicitTlsWithTrustedCertificate()
        {
            using var server = new TestServer(session =>
            {
                session.StartTls(TestCertificate.Localhost);
                session.Expect("secreto");
                session.WriteLine("ok");
            });

            using var connection = NetworkConnection.Connect("localhost", server.Port,
                new ConnectionOptions { Tls = TestCertificate.TrustingOptions }, useTls: true);

            Assert.True(connection.IsSecure);
            connection.WriteLine("secreto");
            Assert.Equal("ok", connection.Reader.ReadLine());
            server.Wait();
        }

        [Fact]
        public void RejectsUntrustedCertificateByDefault()
        {
            using var server = new TestServer(session =>
            {
                try { session.StartTls(TestCertificate.Localhost); } catch (Exception) { }
            });

            Assert.ThrowsAny<AuthenticationException>(() => NetworkConnection.Connect("localhost", server.Port, useTls: true));
        }

        [Fact]
        public void RefusesDataInjectedBeforeStartTls()
        {
            using var server = new TestServer(session =>
            {
                // A man in the middle appends a fake reply that would be read after the upgrade.
                session.Write("220 go ahead\r\n250 injected\r\n");
                Thread.Sleep(200);
            });

            using var connection = NetworkConnection.Connect("127.0.0.1", server.Port);
            Assert.Equal("220 go ahead", connection.Reader.ReadLine());
            Assert.Throws<ProtocolViolationException>(() => connection.UpgradeToTls());
        }

        [Fact]
        public void ReadTimeoutSurfacesAsIOException()
        {
            using var server = new TestServer(_ => Thread.Sleep(1500));

            using var connection = NetworkConnection.Connect("127.0.0.1", server.Port,
                new ConnectionOptions { ReadTimeout = TimeSpan.FromMilliseconds(200) });

            var exception = Assert.Throws<IOException>(() => connection.Reader.ReadLine());
            Assert.Equal(SocketError.TimedOut, Assert.IsType<SocketException>(exception.InnerException).Error);
        }

        [Fact]
        public async Task CancellationAbortsABlockedRead()
        {
            using var server = new TestServer(_ => Thread.Sleep(3000));
            using var connection = NetworkConnection.Connect("127.0.0.1", server.Port);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

            var watch = Stopwatch.StartNew();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection.RunAsync(() => connection.Reader.ReadLine(), cancellation.Token));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2.5), $"Cancelling took {watch.Elapsed}.");
        }
    }
}

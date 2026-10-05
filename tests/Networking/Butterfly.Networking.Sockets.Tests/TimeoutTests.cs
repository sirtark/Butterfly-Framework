using System.Diagnostics;

namespace Butterfly.Networking.Sockets.Tests
{
    public class TimeoutTests
    {
        private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(200);

        [Fact]
        public void TcpReceiveTimesOut()
        {
            using var listener = new TcpSocket();
            listener.Bind(SocketAddress.Loopback(AddressFamily.IPv4, 0));
            listener.Listen();

            using var client = new TcpSocket { ReceiveTimeout = Short };
            client.Connect(listener.LocalAddress);
            using var server = listener.Accept();

            var watch = Stopwatch.StartNew();
            var exception = Assert.Throws<SocketException>(() => client.Receive(new byte[16]));

            Assert.Equal(SocketError.TimedOut, exception.Error);
            Assert.InRange(watch.Elapsed, Short * 0.5, TimeSpan.FromSeconds(5));
        }

        [Fact]
        public void TimeoutCanBeSetAfterOpening()
        {
            using var socket = new UdpSocket();
            socket.Bind(SocketAddress.Loopback(AddressFamily.IPv4, 0));
            socket.ReceiveTimeout = Short;

            var exception = Assert.Throws<SocketException>(() => socket.ReceiveFrom(new byte[16], out _));
            Assert.Equal(SocketError.TimedOut, exception.Error);
            Assert.Equal(Short, socket.ReceiveTimeout);
        }

        [Fact]
        public void ConnectWithTimeoutSucceedsOnReachableEndpoint()
        {
            using var listener = new TcpSocket();
            listener.Bind(SocketAddress.Loopback(AddressFamily.IPv4, 0));
            listener.Listen();

            using var client = new TcpSocket();
            client.Connect(listener.LocalAddress, TimeSpan.FromSeconds(5));

            Assert.Equal(SocketState.Connected, client.State);

            // The socket is blocking again after a bounded connect.
            client.ReceiveTimeout = Short;
            using var server = listener.Accept();
            Assert.Equal(SocketError.TimedOut, Assert.Throws<SocketException>(() => client.Receive(new byte[4])).Error);
        }

        [Fact]
        public void ConnectWithTimeoutReportsRefusal()
        {
            ushort port;
            using (var probe = new TcpSocket())
            {
                probe.Bind(SocketAddress.Loopback(AddressFamily.IPv4, 0));
                port = probe.LocalAddress.Port;
            }

            using var client = new TcpSocket();
            var exception = Assert.Throws<SocketException>(() => client.Connect(SocketAddress.Loopback(AddressFamily.IPv4, port), TimeSpan.FromSeconds(5)));

            Assert.Equal(SocketError.ConnectionRefused, exception.Error);
        }

        [Fact]
        public void ConnectToBlackHoleStopsAtTheTimeout()
        {
            // 192.0.2.0/24 (TEST-NET-1) is never routed: either the packets vanish (timeout) or there is no route at all.
            using var client = new TcpSocket();
            var watch = Stopwatch.StartNew();
            var exception = Assert.Throws<SocketException>(() => client.Connect(SocketAddress.Parse("192.0.2.1", 81), TimeSpan.FromMilliseconds(500)));

            Assert.Contains(exception.Error, new[] { SocketError.TimedOut, SocketError.NetworkUnreachable, SocketError.HostUnreachable });
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"Connect took {watch.Elapsed}.");
        }

        [Fact]
        public async Task AbortWakesABlockedReceive()
        {
            using var listener = new TcpSocket();
            listener.Bind(SocketAddress.Loopback(AddressFamily.IPv4, 0));
            listener.Listen();

            var client = new TcpSocket();
            client.Connect(listener.LocalAddress);
            using var server = listener.Accept();

            var receiving = Task.Run(() =>
            {
                try { return client.Receive(new byte[16]) == 0 ? "eof" : "data"; }
                catch (SocketException ex) { return ex.Error.ToString(); }
            });

            await Task.Delay(200);
            client.Abort();

            Assert.Same(receiving, await Task.WhenAny(receiving, Task.Delay(TimeSpan.FromSeconds(5))));
            Assert.Contains(await receiving, new[] { "eof", nameof(SocketError.OperationAborted), nameof(SocketError.Interrupted) });
            Assert.Equal(SocketState.Closed, client.State);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-5)]
        public void RejectsInvalidTimeouts(int milliseconds)
        {
            using var socket = new TcpSocket();
            Assert.Throws<ArgumentOutOfRangeException>(() => socket.ReceiveTimeout = TimeSpan.FromMilliseconds(milliseconds));
        }
    }
}

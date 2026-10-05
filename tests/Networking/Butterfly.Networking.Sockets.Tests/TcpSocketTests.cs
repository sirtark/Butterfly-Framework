using System.Text;

namespace Butterfly.Networking.Sockets.Tests
{
    public class TcpSocketTests
    {
        [Theory]
        [InlineData(AddressFamily.IPv4)]
        [InlineData(AddressFamily.IPv6)]
        public void EchoesOverLoopback(AddressFamily family)
        {
            using var listener = new TcpSocket(family);
            listener.Bind(SocketAddress.Loopback(family, 0));
            listener.Listen();

            var endpoint = listener.LocalAddress;
            Assert.NotEqual(0, endpoint.Port);

            using var client = new TcpSocket(family, new TcpSocketOptions { NoDelay = true });
            client.Connect(endpoint);

            // The connection is already queued in the backlog, so this does not block.
            using var server = listener.Accept();

            Assert.Equal(SocketState.Connected, client.State);
            Assert.Equal(SocketState.Connected, server.State);
            Assert.Equal(endpoint, client.RemoteAddress);
            Assert.Equal(client.LocalAddress, server.RemoteAddress);

            byte[] message = Encoding.UTF8.GetBytes("hola, butterfly");
            SendAll(client, message);
            byte[] received = ReceiveExactly(server, message.Length);
            SendAll(server, received);

            Assert.Equal(message, ReceiveExactly(client, message.Length));

            client.Shutdown(SocketShutdown.Send);
            Assert.Equal(0, server.Receive(new byte[16]));
        }

        [Fact]
        public void ConnectToClosedPortIsRefused()
        {
            ushort port;
            using (var probe = new TcpSocket())
            {
                probe.Bind(SocketAddress.Loopback(AddressFamily.IPv4, 0));
                port = probe.LocalAddress.Port;
            }

            using var client = new TcpSocket();
            var exception = Assert.Throws<SocketException>(() => client.Connect(SocketAddress.Loopback(AddressFamily.IPv4, port)));

            Assert.Equal(SocketError.ConnectionRefused, exception.Error);
            Assert.NotEqual(0, exception.NativeErrorCode);
            Assert.Equal("connect", exception.Operation);
        }

        [Fact]
        public void BindingAnActivePortFails()
        {
            using var first = new TcpSocket();
            first.Bind(SocketAddress.Loopback(AddressFamily.IPv4, 0));
            first.Listen();

            using var second = new TcpSocket(options: new TcpSocketOptions { ReuseAddress = true });
            var exception = Assert.Throws<SocketException>(() => second.Bind(first.LocalAddress));

            Assert.Equal(SocketError.AddressInUse, exception.Error);
        }

        [Fact]
        public void RebindsPortWithConnectionInTimeWait()
        {
            SocketAddress endpoint;
            using (var listener = new TcpSocket())
            {
                listener.Bind(SocketAddress.Loopback(AddressFamily.IPv4, 0));
                listener.Listen();
                endpoint = listener.LocalAddress;

                using var client = new TcpSocket();
                client.Connect(endpoint);
                using var server = listener.Accept();

                // The server closes first, which leaves its side of the connection in TIME_WAIT on the listening port.
                server.Shutdown(SocketShutdown.Both);
                Assert.Equal(0, client.Receive(new byte[1]));
                server.Close();
                client.Close();
            }

            using var rebound = new TcpSocket(options: new TcpSocketOptions { ReuseAddress = true });
            rebound.Bind(endpoint);
            rebound.Listen();

            Assert.Equal(SocketState.Listening, rebound.State);
        }

        [Fact]
        public void TracksStateTransitions()
        {
            using var socket = new TcpSocket();
            Assert.Equal(SocketState.Created, socket.State);

            socket.Open();
            Assert.Equal(SocketState.Open, socket.State);

            socket.Bind(SocketAddress.Loopback(AddressFamily.IPv4, 0));
            Assert.Equal(SocketState.Bound, socket.State);

            socket.Listen();
            Assert.Equal(SocketState.Listening, socket.State);

            socket.Close();
            Assert.Equal(SocketState.Closed, socket.State);
        }

        [Fact]
        public void RejectsInvalidOperations()
        {
            using var socket = new TcpSocket();

            Assert.Throws<InvalidOperationException>(() => socket.Listen());
            Assert.Throws<InvalidOperationException>(() => socket.Accept());
            Assert.Throws<InvalidOperationException>(() => socket.Send([1]));
            Assert.Throws<InvalidOperationException>(() => _ = socket.LocalAddress);

            socket.Open();
            Assert.Throws<InvalidOperationException>(() => socket.Open());
            Assert.Throws<ArgumentException>(() => socket.Bind(SocketAddress.Loopback(AddressFamily.IPv6, 0)));
        }

        [Fact]
        public void ThrowsAfterDispose()
        {
            var socket = new TcpSocket();
            socket.Open();
            socket.Dispose();
            socket.Dispose();

            Assert.Throws<ObjectDisposedException>(() => socket.Bind(SocketAddress.Any(AddressFamily.IPv4, 0)));
            Assert.Throws<ObjectDisposedException>(() => _ = socket.LocalAddress);
        }

        [Fact]
        public void RejectsUnspecifiedFamily()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new TcpSocket(AddressFamily.Unspecified));
        }

        private static void SendAll(TcpSocket socket, ReadOnlySpan<byte> data)
        {
            while (!data.IsEmpty)
                data = data[socket.Send(data)..];
        }

        private static byte[] ReceiveExactly(TcpSocket socket, int length)
        {
            byte[] buffer = new byte[length];
            int total = 0;

            while (total < length)
            {
                int read = socket.Receive(buffer.AsSpan(total));
                Assert.NotEqual(0, read);
                total += read;
            }

            return buffer;
        }
    }
}

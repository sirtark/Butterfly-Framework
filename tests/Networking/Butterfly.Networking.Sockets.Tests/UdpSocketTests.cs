using System.Text;

namespace Butterfly.Networking.Sockets.Tests
{
    // Loopback datagrams are delivered before sendto returns, so none of these receives block.
    public class UdpSocketTests
    {
        [Theory]
        [InlineData(AddressFamily.IPv4)]
        [InlineData(AddressFamily.IPv6)]
        public void EchoesOverLoopback(AddressFamily family)
        {
            using var server = new UdpSocket(family);
            server.Bind(SocketAddress.Loopback(family, 0));
            var serverAddress = server.LocalAddress;

            using var client = new UdpSocket(family);
            byte[] message = Encoding.UTF8.GetBytes("hola, butterfly");
            Assert.Equal(message.Length, client.SendTo(message, serverAddress));

            byte[] buffer = new byte[UdpSocket.MaxDatagramSize];
            int received = server.ReceiveFrom(buffer, out var clientAddress);

            Assert.Equal(message, buffer[..received]);
            Assert.Equal(client.LocalAddress.Port, clientAddress.Port);

            server.SendTo(buffer.AsSpan(0, received), clientAddress);
            received = client.ReceiveFrom(buffer, out var replyAddress);

            Assert.Equal(message, buffer[..received]);
            Assert.Equal(serverAddress, replyAddress);
        }

        [Fact]
        public void SendToBindsToAnEphemeralPort()
        {
            using var server = new UdpSocket();
            server.Bind(SocketAddress.Loopback(AddressFamily.IPv4, 0));

            using var client = new UdpSocket();
            client.SendTo([1], server.LocalAddress);

            Assert.Equal(SocketState.Bound, client.State);
            Assert.NotEqual(0, client.LocalAddress.Port);
        }

        [Fact]
        public void ConnectedSocketUsesSendAndReceive()
        {
            using var server = new UdpSocket();
            server.Bind(SocketAddress.Loopback(AddressFamily.IPv4, 0));

            using var client = new UdpSocket();
            client.Connect(server.LocalAddress);
            Assert.Equal(SocketState.Connected, client.State);
            Assert.Equal(server.LocalAddress, client.RemoteAddress);

            client.Send([1, 2, 3]);
            byte[] buffer = new byte[16];
            int received = server.ReceiveFrom(buffer, out var clientAddress);
            Assert.Equal([1, 2, 3], buffer[..received]);

            server.SendTo([4, 5], clientAddress);
            received = client.Receive(buffer);
            Assert.Equal([4, 5], buffer[..received]);
        }

        [Fact]
        public void ConnectedSocketIgnoresOtherSenders()
        {
            using var peer = new UdpSocket();
            peer.Bind(SocketAddress.Loopback(AddressFamily.IPv4, 0));

            using var stranger = new UdpSocket();
            stranger.Bind(SocketAddress.Loopback(AddressFamily.IPv4, 0));

            using var socket = new UdpSocket();
            socket.Bind(SocketAddress.Loopback(AddressFamily.IPv4, 0));
            socket.Connect(peer.LocalAddress);

            stranger.SendTo([9], socket.LocalAddress);
            peer.SendTo([1], socket.LocalAddress);

            byte[] buffer = new byte[16];
            int received = socket.Receive(buffer);
            Assert.Equal([1], buffer[..received]);
        }

        [Fact]
        public void TruncatesOversizedDatagram()
        {
            using var server = new UdpSocket();
            server.Bind(SocketAddress.Loopback(AddressFamily.IPv4, 0));

            using var client = new UdpSocket();
            client.SendTo([1, 2, 3, 4, 5, 6, 7, 8], server.LocalAddress);
            client.SendTo([9, 10], server.LocalAddress);

            byte[] small = new byte[4];
            Assert.Equal(4, server.ReceiveFrom(small, out var from));
            Assert.Equal([1, 2, 3, 4], small);
            Assert.Equal(client.LocalAddress.Port, from.Port);

            // The rest of the first datagram is gone; the next one arrives whole.
            byte[] buffer = new byte[16];
            int received = server.ReceiveFrom(buffer, out _);
            Assert.Equal([9, 10], buffer[..received]);
        }

        [Fact]
        public void SendingToAClosedPortDoesNotBreakReceive()
        {
            ushort closedPort;
            using (var probe = new UdpSocket())
            {
                probe.Bind(SocketAddress.Loopback(AddressFamily.IPv4, 0));
                closedPort = probe.LocalAddress.Port;
            }

            using var socket = new UdpSocket();
            socket.Bind(SocketAddress.Loopback(AddressFamily.IPv4, 0));

            // Triggers an ICMP "port unreachable" that Windows would otherwise report on the next recvfrom.
            socket.SendTo([1], SocketAddress.Loopback(AddressFamily.IPv4, closedPort));
            socket.SendTo([2], socket.LocalAddress);

            byte[] buffer = new byte[16];
            int received = socket.ReceiveFrom(buffer, out _);
            Assert.Equal([2], buffer[..received]);
        }

        [Fact]
        public void BroadcastRequiresTheOption()
        {
            var broadcast = SocketAddress.Parse("255.255.255.255", 9);

            using var socket = new UdpSocket();
            var exception = Assert.Throws<SocketException>(() => socket.SendTo([1], broadcast));
            Assert.Equal(SocketError.AccessDenied, exception.Error);
            Assert.Equal("sendto", exception.Operation);

            using var allowed = new UdpSocket(options: new UdpSocketOptions { Broadcast = true });
            try
            {
                allowed.SendTo([1], broadcast);
            }
            catch (SocketException e)
            {
                // A machine without a broadcast-capable interface may still fail, but not for lack of permission.
                Assert.NotEqual(SocketError.AccessDenied, e.Error);
            }
        }

        [Fact]
        public void RejectsInvalidOperations()
        {
            using var socket = new UdpSocket();

            Assert.Throws<InvalidOperationException>(() => socket.ReceiveFrom(new byte[1], out _));
            Assert.Throws<InvalidOperationException>(() => socket.Send([1]));
            Assert.Throws<InvalidOperationException>(() => socket.Receive(new byte[1]));

            socket.Open();
            Assert.Throws<InvalidOperationException>(() => socket.ReceiveFrom(new byte[1], out _));
            Assert.Throws<ArgumentException>(() => socket.SendTo([1], SocketAddress.Loopback(AddressFamily.IPv6, 9)));
            Assert.Throws<ArgumentException>(() => socket.Connect(SocketAddress.Loopback(AddressFamily.IPv6, 9)));

            socket.Connect(SocketAddress.Loopback(AddressFamily.IPv4, 9));
            Assert.Throws<InvalidOperationException>(() => socket.SendTo([1], SocketAddress.Loopback(AddressFamily.IPv4, 9)));
        }

        [Fact]
        public void ThrowsAfterDispose()
        {
            var socket = new UdpSocket();
            socket.Bind(SocketAddress.Loopback(AddressFamily.IPv4, 0));
            socket.Dispose();

            Assert.Throws<ObjectDisposedException>(() => socket.SendTo([1], SocketAddress.Loopback(AddressFamily.IPv4, 9)));
            Assert.Throws<ObjectDisposedException>(() => socket.ReceiveFrom(new byte[1], out _));
        }
    }
}

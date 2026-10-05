using System.Runtime.Versioning;
using Butterfly.Networking.Sockets.Native;

namespace Butterfly.Networking.Sockets
{
    /// <remarks>
    /// A datagram larger than the receive buffer is truncated to fit and the rest is discarded, on every platform.
    /// ICMP errors caused by earlier sends (such as "port unreachable") are not reported on an unconnected socket.
    /// On a connected socket, Unix reports them on the next receive as <see cref="SocketError.ConnectionRefused"/>; Windows does not.
    /// </remarks>
    [UnsupportedOSPlatform("browser")]
    [UnsupportedOSPlatform("wasi")]
    public sealed class UdpSocket : Socket
    {
        /// <summary>The largest UDP payload over IPv4: 65535 minus the IPv4 and UDP headers.</summary>
        public const int MaxDatagramSize = 65507;

        public UdpSocket(AddressFamily addressFamily = AddressFamily.IPv4, UdpSocketOptions? options = null)
            : base(addressFamily, SocketType.Datagram, Protocol.Udp, options ?? new UdpSocketOptions())
        {
        }

        public new UdpSocketOptions Options => (UdpSocketOptions)base.Options;

        public int SendTo(ReadOnlySpan<byte> buffer, SocketAddress remoteAddress)
        {
            EnsureOpen();
            RequireState(nameof(SendTo), SocketState.Open, SocketState.Bound);
            ThrowIfFamilyMismatch(remoteAddress);

            int sent = Platform.SendTo(Handle, buffer, remoteAddress);

            // Sending binds an unbound socket to an ephemeral port, so replies can now be received.
            State = SocketState.Bound;
            return sent;
        }

        /// <returns>The number of bytes received; a datagram larger than <paramref name="buffer"/> is truncated to fit.</returns>
        public int ReceiveFrom(Span<byte> buffer, out SocketAddress remoteAddress)
        {
            RequireState(nameof(ReceiveFrom), SocketState.Bound, SocketState.Connected);
            return Platform.ReceiveFrom(Handle, buffer, out remoteAddress);
        }

        /// <summary>
        /// Sets the default destination for <see cref="Send"/> and only accepts datagrams from it.
        /// It can be called again to change the destination.
        /// </summary>
        public void Connect(SocketAddress remoteAddress)
        {
            EnsureOpen();
            RequireState(nameof(Connect), SocketState.Open, SocketState.Bound, SocketState.Connected);
            ThrowIfFamilyMismatch(remoteAddress);

            Platform.Connect(Handle, remoteAddress, Timeout.InfiniteTimeSpan);
            State = SocketState.Connected;
        }

        public int Send(ReadOnlySpan<byte> buffer)
        {
            RequireState(nameof(Send), SocketState.Connected);
            return Platform.Send(Handle, buffer);
        }

        /// <returns>The number of bytes received; a datagram larger than <paramref name="buffer"/> is truncated to fit.</returns>
        public int Receive(Span<byte> buffer)
        {
            RequireState(nameof(Receive), SocketState.Connected);
            return Platform.Receive(Handle, buffer);
        }

        private protected override void OnOpened(SafeSocketHandle handle)
            => Platform.SetOption(handle, SocketOption.Broadcast, Options.Broadcast);
    }
}

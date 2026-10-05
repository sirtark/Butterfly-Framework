using System.Runtime.Versioning;
using Butterfly.Networking.Sockets.Native;

namespace Butterfly.Networking.Sockets
{
    [UnsupportedOSPlatform("browser")]
    [UnsupportedOSPlatform("wasi")]
    public sealed class TcpSocket : Socket
    {
        public const int DefaultBacklog = 128;

        public TcpSocket(AddressFamily addressFamily = AddressFamily.IPv4, TcpSocketOptions? options = null)
            : base(addressFamily, SocketType.Stream, Protocol.Tcp, options ?? new TcpSocketOptions())
        {
        }

        private TcpSocket(SafeSocketHandle handle, AddressFamily addressFamily, TcpSocketOptions options)
            : base(handle, addressFamily, SocketType.Stream, Protocol.Tcp, options, SocketState.Connected)
        {
        }

        public new TcpSocketOptions Options => (TcpSocketOptions)base.Options;

        public void Listen(int backlog = DefaultBacklog)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(backlog);
            RequireState(nameof(Listen), SocketState.Bound);

            Platform.Listen(Handle, backlog);
            State = SocketState.Listening;
        }

        public TcpSocket Accept()
        {
            RequireState(nameof(Accept), SocketState.Listening);

            // Which options an accepted socket inherits from the listener varies by OS, so they are applied again.
            var handle = Platform.Accept(Handle);
            try
            {
                ApplyTcpOptions(handle);
            }
            catch
            {
                handle.Dispose();
                throw;
            }

            return new TcpSocket(handle, AddressFamily, Options);
        }

        public void Connect(SocketAddress remoteAddress) => Connect(remoteAddress, Timeout.InfiniteTimeSpan);

        /// <summary>
        /// Connects, failing with <see cref="SocketError.TimedOut"/> if the connection is not established within
        /// <paramref name="timeout"/>. After a failed attempt the socket cannot be reused; create a new one.
        /// </summary>
        public void Connect(SocketAddress remoteAddress, TimeSpan timeout)
        {
            ValidateTimeout(timeout, nameof(timeout));
            EnsureOpen();
            RequireState(nameof(Connect), SocketState.Open, SocketState.Bound);
            ThrowIfFamilyMismatch(remoteAddress);

            Platform.Connect(Handle, remoteAddress, timeout);
            State = SocketState.Connected;
        }

        public int Send(ReadOnlySpan<byte> buffer)
        {
            RequireState(nameof(Send), SocketState.Connected);
            return Platform.Send(Handle, buffer);
        }

        /// <returns>The number of bytes received, or 0 when the peer has shut down its sending side.</returns>
        public int Receive(Span<byte> buffer)
        {
            RequireState(nameof(Receive), SocketState.Connected);
            return Platform.Receive(Handle, buffer);
        }

        public void Shutdown(SocketShutdown how)
        {
            RequireState(nameof(Shutdown), SocketState.Connected);
            Platform.Shutdown(Handle, how);
        }

        private protected override void OnOpened(SafeSocketHandle handle) => ApplyTcpOptions(handle);

        private void ApplyTcpOptions(SafeSocketHandle handle)
        {
            Platform.SetOption(handle, SocketOption.NoDelay, Options.NoDelay);
            Platform.SetOption(handle, SocketOption.KeepAlive, Options.KeepAlive);
        }
    }
}

using System.Runtime.Versioning;
using Butterfly.Networking.Sockets.Native;

namespace Butterfly.Networking.Sockets
{
    [UnsupportedOSPlatform("browser")]
    [UnsupportedOSPlatform("wasi")]
    public abstract class Socket : IDisposable
    {
        private SafeSocketHandle? _handle;

        private protected Socket(AddressFamily addressFamily, SocketType type, Protocol protocol, SocketOptions options)
        {
            if (addressFamily is not (AddressFamily.IPv4 or AddressFamily.IPv6))
                throw new ArgumentOutOfRangeException(nameof(addressFamily), addressFamily, "A socket requires IPv4 or IPv6.");

            AddressFamily = addressFamily;
            Type = type;
            Protocol = protocol;
            Options = options;
        }

        private protected Socket(SafeSocketHandle handle, AddressFamily addressFamily, SocketType type, Protocol protocol, SocketOptions options, SocketState state) : this(addressFamily, type, protocol, options)
        {
            _handle = handle;
            State = state;
        }

        public AddressFamily AddressFamily { get; }
        public SocketType Type { get; }
        public Protocol Protocol { get; }
        public SocketOptions Options { get; }
        public SocketState State { get; private protected set; } = SocketState.Created;

        public SocketAddress LocalAddress => Platform.GetLocalAddress(Handle);
        public SocketAddress RemoteAddress => Platform.GetRemoteAddress(Handle);

        /// <summary>
        /// How long a blocking receive waits before failing with <see cref="SocketError.TimedOut"/>.
        /// <see cref="Timeout.InfiniteTimeSpan"/> (the default) waits forever. It can be changed at any time.
        /// </summary>
        /// <remarks>After a timeout the state of a TCP stream is undefined (Winsock), so the connection should be closed.</remarks>
        public TimeSpan ReceiveTimeout
        {
            get => _receiveTimeout;
            set => _receiveTimeout = ApplyTimeout(SocketTimeout.Receive, value);
        }

        /// <summary>How long a blocking send waits before failing with <see cref="SocketError.TimedOut"/>.</summary>
        public TimeSpan SendTimeout
        {
            get => _sendTimeout;
            set => _sendTimeout = ApplyTimeout(SocketTimeout.Send, value);
        }

        private TimeSpan _receiveTimeout = Timeout.InfiniteTimeSpan;
        private TimeSpan _sendTimeout = Timeout.InfiniteTimeSpan;

        private protected static ISocketPlatform Platform => SocketPlatform.Current;

        private protected SafeSocketHandle Handle
        {
            get
            {
                ObjectDisposedException.ThrowIf(State == SocketState.Closed, this);
                return _handle ?? throw new InvalidOperationException("The socket has not been opened.");
            }
        }

        public void Open()
        {
            RequireState(nameof(Open), SocketState.Created);

            var handle = Platform.Create(AddressFamily, Type, Protocol);
            try
            {
                Platform.SetOption(handle, SocketOption.ReuseAddress, Options.ReuseAddress);

                if (_receiveTimeout != Timeout.InfiniteTimeSpan)
                    Platform.SetTimeout(handle, SocketTimeout.Receive, _receiveTimeout);
                if (_sendTimeout != Timeout.InfiniteTimeSpan)
                    Platform.SetTimeout(handle, SocketTimeout.Send, _sendTimeout);

                OnOpened(handle);
            }
            catch
            {
                handle.Dispose();
                throw;
            }

            _handle = handle;
            State = SocketState.Open;
        }

        public void Bind(SocketAddress address)
        {
            EnsureOpen();
            RequireState(nameof(Bind), SocketState.Open);
            ThrowIfFamilyMismatch(address);

            Platform.Bind(Handle, address);
            State = SocketState.Bound;
        }

        public void Close()
        {
            if (State == SocketState.Closed)
                return;

            State = SocketState.Closed;
            _handle?.Dispose();
        }

        public void Dispose() => Close();

        /// <summary>
        /// Closes the socket and wakes up any call blocked on it in another thread: such calls fail with
        /// <see cref="SocketError.OperationAborted"/> or report the end of the stream. <see cref="Close"/> alone
        /// waits for blocked calls to return first.
        /// </summary>
        public void Abort()
        {
            if (State == SocketState.Closed)
                return;

            if (_handle is { IsClosed: false } handle)
            {
                try
                {
                    Platform.Abort(handle);
                }
                catch (ObjectDisposedException)
                {
                }
            }

            Close();
        }

        private protected virtual void OnOpened(SafeSocketHandle handle)
        {
        }

        private protected static void ValidateTimeout(TimeSpan timeout, string paramName)
        {
            if (timeout != Timeout.InfiniteTimeSpan && (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue))
                throw new ArgumentOutOfRangeException(paramName, timeout, "A timeout must be positive (and fit in Int32 milliseconds) or Timeout.InfiniteTimeSpan.");
        }

        // Stored for sockets that are not open yet (applied by Open) and set right away on open ones.
        private TimeSpan ApplyTimeout(SocketTimeout kind, TimeSpan timeout)
        {
            ValidateTimeout(timeout, "value");
            ObjectDisposedException.ThrowIf(State == SocketState.Closed, this);

            if (_handle is not null)
                Platform.SetTimeout(_handle, kind, timeout);

            return timeout;
        }

        private protected void EnsureOpen()
        {
            if (State == SocketState.Created) Open();
        }

        private protected void RequireState(string operation, params ReadOnlySpan<SocketState> allowed)
        {
            ObjectDisposedException.ThrowIf(State == SocketState.Closed, this);

            if (!allowed.Contains(State))
                throw new InvalidOperationException($"{operation} is not valid while the socket is {State}.");
        }

        private protected void ThrowIfFamilyMismatch(in SocketAddress address)
        {
            if (address.Family != AddressFamily)
                throw new ArgumentException($"Address '{address}' is {address.Family}, but the socket is {AddressFamily}.", nameof(address));
        }
    }
}

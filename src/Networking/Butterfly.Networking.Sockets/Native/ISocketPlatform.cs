using System.Runtime.InteropServices;
using Butterfly.Networking.Sockets.Native.Unix;
using Butterfly.Networking.Sockets.Native.Windows;

namespace Butterfly.Networking.Sockets.Native
{
    internal interface ISocketPlatform
    {
        SafeSocketHandle Create(AddressFamily addressFamily, SocketType type, Protocol protocol);
        bool Close(nint handle);
        void SetOption(SafeSocketHandle handle, SocketOption option, bool value);

        /// <summary>Limits how long a blocking send or receive waits; <see cref="Timeout.InfiniteTimeSpan"/> waits forever.</summary>
        void SetTimeout(SafeSocketHandle handle, SocketTimeout kind, TimeSpan timeout);
        void Bind(SafeSocketHandle handle, in SocketAddress address);
        void Listen(SafeSocketHandle handle, int backlog);
        SafeSocketHandle Accept(SafeSocketHandle handle);

        /// <summary>Fails with <see cref="SocketError.TimedOut"/> when <paramref name="timeout"/> elapses first.</summary>
        void Connect(SafeSocketHandle handle, in SocketAddress address, TimeSpan timeout);
        int Send(SafeSocketHandle handle, ReadOnlySpan<byte> buffer);
        int Receive(SafeSocketHandle handle, Span<byte> buffer);
        int SendTo(SafeSocketHandle handle, ReadOnlySpan<byte> buffer, in SocketAddress address);

        /// <summary>A datagram larger than <paramref name="buffer"/> is truncated to fit, on every platform.</summary>
        int ReceiveFrom(SafeSocketHandle handle, Span<byte> buffer, out SocketAddress address);
        void Shutdown(SafeSocketHandle handle, SocketShutdown how);

        /// <summary>Wakes up every call blocked on the socket in other threads; they fail or see end of stream.</summary>
        void Abort(SafeSocketHandle handle);
        SocketAddress GetLocalAddress(SafeSocketHandle handle);
        SocketAddress GetRemoteAddress(SafeSocketHandle handle);
    }

    internal enum SocketOption
    {
        ReuseAddress,
        KeepAlive,
        NoDelay,
        Broadcast
    }

    internal enum SocketTimeout
    {
        Receive,
        Send
    }

    internal static class SocketPlatform
    {
        /// <summary>Whole milliseconds for native timeouts: 0 means "wait forever", so positive values round up to at least 1.</summary>
        public static int ToMilliseconds(TimeSpan timeout)
            => timeout == Timeout.InfiniteTimeSpan ? 0 : (int)Math.Max(1, Math.Ceiling(timeout.TotalMilliseconds));

        private static readonly Lazy<ISocketPlatform> s_current = new(Create);

        public static ISocketPlatform Current => s_current.Value;

        private static ISocketPlatform Create()
        {
            if (OperatingSystem.IsWindows())
                return new WindowsSocketPlatform();

            if (OperatingSystem.IsLinux() || OperatingSystem.IsAndroid())
                return new UnixSocketPlatform(UnixPlatformInfo.Linux);

            if (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() || OperatingSystem.IsTvOS()
                || OperatingSystem.IsWatchOS() || OperatingSystem.IsMacCatalyst())
                return new UnixSocketPlatform(UnixPlatformInfo.Apple);

            if (OperatingSystem.IsFreeBSD())
                return new UnixSocketPlatform(UnixPlatformInfo.FreeBSD);

            if (OperatingSystem.IsBrowser())
                throw new PlatformNotSupportedException("Browsers do not allow raw TCP/UDP sockets. Use WebSockets or WebTransport, or a WebSocket-to-TCP relay.");

            if (OperatingSystem.IsWasi())
                throw new PlatformNotSupportedException("WASI sockets (wasi:sockets, WASI preview 2) are not yet reachable from .NET.");

            throw new PlatformNotSupportedException($"Sockets are not supported on '{RuntimeInformation.OSDescription}'.");
        }
    }
}

using System.Runtime.InteropServices;

namespace Butterfly.Networking.Sockets.Native.Unix
{
    internal sealed unsafe class UnixSocketPlatform : ISocketPlatform
    {
        private const int SockStream = 1;
        private const int SockDgram = 2;
        private const int IpProtoTcp = 6;
        private const int IpProtoUdp = 17;
        private const int IpProtoIpv6 = 41;
        private const int TcpNoDelay = 1;
        private const short PollOut = 0x0004;

        private readonly UnixPlatformInfo _info;

        public UnixSocketPlatform(UnixPlatformInfo info)
        {
            _info = info;
            LibC.RegisterResolver();
        }

        public SafeSocketHandle Create(AddressFamily addressFamily, SocketType type, Protocol protocol)
        {
            int domain = addressFamily switch
            {
                AddressFamily.IPv4 => _info.SockAddr.AfInet,
                AddressFamily.IPv6 => _info.SockAddr.AfInet6,
                _ => throw new NotSupportedException($"Address family '{addressFamily}' is not supported.")
            };

            int fd = LibC.Socket(domain, ToNative(type) | _info.SockCloexec, ToNative(protocol));
            if (fd == -1)
                throw LastError("socket");

            var handle = Wrap(fd);
            try
            {
                ConfigureDescriptor(fd);

                // Linux and BSD default to dual-stack IPv6 sockets; Windows does not. Match Windows.
                if (addressFamily == AddressFamily.IPv6)
                    SetInt(fd, IpProtoIpv6, _info.Ipv6V6Only, 1);
            }
            catch
            {
                handle.Dispose();
                throw;
            }

            return handle;
        }

        public bool Close(nint handle)
        {
            // close() must not be retried on EINTR: the descriptor is already released on Linux.
            return LibC.Close((int)handle) == 0 || Marshal.GetLastPInvokeError() == UnixErrors.EINTR;
        }

        public void SetOption(SafeSocketHandle handle, SocketOption option, bool value)
        {
            (int level, int name) = option switch
            {
                SocketOption.ReuseAddress => (_info.SolSocket, _info.SoReuseAddr),
                SocketOption.KeepAlive => (_info.SolSocket, _info.SoKeepAlive),
                SocketOption.NoDelay => (IpProtoTcp, TcpNoDelay),
                SocketOption.Broadcast => (_info.SolSocket, _info.SoBroadcast),
                _ => throw new ArgumentOutOfRangeException(nameof(option), option, null)
            };

            using var lease = new HandleLease(handle);
            SetInt(lease.Fd, level, name, value ? 1 : 0);
        }

        public void SetTimeout(SafeSocketHandle handle, SocketTimeout kind, TimeSpan timeout)
        {
            int name = kind == SocketTimeout.Receive ? _info.SoRcvTimeo : _info.SoSndTimeo;
            long milliseconds = SocketPlatform.ToMilliseconds(timeout);
            var value = new TimeVal { Seconds = (nint)(milliseconds / 1000), Microseconds = (nint)(milliseconds % 1000 * 1000) };

            using var lease = new HandleLease(handle);
            ThrowIfError(LibC.SetSockOpt(lease.Fd, _info.SolSocket, name, &value, (uint)sizeof(TimeVal)), "setsockopt");
        }

        public void Bind(SafeSocketHandle handle, in SocketAddress address)
        {
            Span<byte> buffer = stackalloc byte[SockAddrLayout.MaxLength];
            int length = _info.SockAddr.Write(address, buffer);

            using var lease = new HandleLease(handle);
            fixed (byte* sockaddr = buffer)
                ThrowIfError(LibC.Bind(lease.Fd, sockaddr, (uint)length), "bind");
        }

        public void Listen(SafeSocketHandle handle, int backlog)
        {
            using var lease = new HandleLease(handle);
            ThrowIfError(LibC.Listen(lease.Fd, backlog), "listen");
        }

        public SafeSocketHandle Accept(SafeSocketHandle handle)
        {
            using var lease = new HandleLease(handle);

            int fd;
            do
            {
                fd = _info.HasAccept4
                    ? LibC.Accept4(lease.Fd, null, null, _info.SockCloexec)
                    : LibC.Accept(lease.Fd, null, null);
            }
            while (fd == -1 && Marshal.GetLastPInvokeError() == UnixErrors.EINTR);

            if (fd == -1)
                throw LastError("accept");

            var accepted = Wrap(fd);
            try
            {
                ConfigureDescriptor(fd);
            }
            catch
            {
                accepted.Dispose();
                throw;
            }

            return accepted;
        }

        public void Connect(SafeSocketHandle handle, in SocketAddress address, TimeSpan timeout)
        {
            Span<byte> buffer = stackalloc byte[SockAddrLayout.MaxLength];
            int length = _info.SockAddr.Write(address, buffer);

            using var lease = new HandleLease(handle);

            // With a timeout, connect without blocking and let poll enforce the limit.
            bool bounded = timeout != Timeout.InfiniteTimeSpan;
            int flags = 0;
            if (bounded)
            {
                flags = LibC.FcntlGet(lease.Fd, LibC.FGetFl);
                ThrowIfError(flags, "fcntl(F_GETFL)");
                ThrowIfError(LibC.FcntlSet(lease.Fd, LibC.FSetFl, flags | _info.ONonBlock), "fcntl(F_SETFL)");
            }

            try
            {
                int result;
                fixed (byte* sockaddr = buffer)
                    result = LibC.Connect(lease.Fd, sockaddr, (uint)length);

                if (result == 0)
                    return;

                int error = Marshal.GetLastPInvokeError();
                if (error != UnixErrors.EINTR && !(bounded && error == _info.EInProgress))
                    throw CreateException(error, "connect");

                // An interrupted or non-blocking connect keeps going in the background and cannot be restarted; wait for it.
                WaitForPendingConnect(lease.Fd, bounded ? SocketPlatform.ToMilliseconds(timeout) : -1);
            }
            finally
            {
                if (bounded)
                    ThrowIfError(LibC.FcntlSet(lease.Fd, LibC.FSetFl, flags), "fcntl(F_SETFL)");
            }
        }

        public int Send(SafeSocketHandle handle, ReadOnlySpan<byte> buffer)
        {
            using var lease = new HandleLease(handle);

            fixed (byte* data = &MemoryMarshal.GetReference(buffer))
            {
                nint sent;
                do
                {
                    sent = LibC.Send(lease.Fd, data, (nuint)buffer.Length, _info.MsgNoSignal);
                }
                while (sent == -1 && Marshal.GetLastPInvokeError() == UnixErrors.EINTR);

                if (sent == -1)
                    throw LastError("send");

                return (int)sent;
            }
        }

        public int Receive(SafeSocketHandle handle, Span<byte> buffer)
        {
            using var lease = new HandleLease(handle);

            fixed (byte* data = &MemoryMarshal.GetReference(buffer))
            {
                nint received;
                do
                {
                    received = LibC.Recv(lease.Fd, data, (nuint)buffer.Length, 0);
                }
                while (received == -1 && Marshal.GetLastPInvokeError() == UnixErrors.EINTR);

                if (received == -1)
                    throw LastError("recv");

                return (int)received;
            }
        }

        public int SendTo(SafeSocketHandle handle, ReadOnlySpan<byte> buffer, in SocketAddress address)
        {
            Span<byte> addressBuffer = stackalloc byte[SockAddrLayout.MaxLength];
            int addressLength = _info.SockAddr.Write(address, addressBuffer);

            using var lease = new HandleLease(handle);

            fixed (byte* data = &MemoryMarshal.GetReference(buffer))
            fixed (byte* sockaddr = addressBuffer)
            {
                nint sent;
                do
                {
                    sent = LibC.SendTo(lease.Fd, data, (nuint)buffer.Length, 0, sockaddr, (uint)addressLength);
                }
                while (sent == -1 && Marshal.GetLastPInvokeError() == UnixErrors.EINTR);

                if (sent == -1)
                    throw LastError("sendto");

                return (int)sent;
            }
        }

        public int ReceiveFrom(SafeSocketHandle handle, Span<byte> buffer, out SocketAddress address)
        {
            Span<byte> addressBuffer = stackalloc byte[SockAddrLayout.MaxLength];
            uint addressLength;

            using (var lease = new HandleLease(handle))
            {
                fixed (byte* data = &MemoryMarshal.GetReference(buffer))
                fixed (byte* sockaddr = addressBuffer)
                {
                    nint received;
                    do
                    {
                        addressLength = (uint)addressBuffer.Length;
                        received = LibC.RecvFrom(lease.Fd, data, (nuint)buffer.Length, 0, sockaddr, &addressLength);
                    }
                    while (received == -1 && Marshal.GetLastPInvokeError() == UnixErrors.EINTR);

                    if (received == -1)
                        throw LastError("recvfrom");

                    address = _info.SockAddr.Read(addressBuffer[..(int)addressLength]);
                    return (int)received;
                }
            }
        }

        public void Shutdown(SafeSocketHandle handle, SocketShutdown how)
        {
            using var lease = new HandleLease(handle);
            ThrowIfError(LibC.Shutdown(lease.Fd, (int)how), "shutdown");
        }

        // shutdown(SHUT_RDWR) wakes blocked recv/send calls (they see end of stream or EPIPE); errors such as
        // ENOTCONN on a socket that never connected are irrelevant here.
        public void Abort(SafeSocketHandle handle)
        {
            using var lease = new HandleLease(handle);
            LibC.Shutdown(lease.Fd, (int)SocketShutdown.Both);
        }

        public SocketAddress GetLocalAddress(SafeSocketHandle handle)
        {
            Span<byte> buffer = stackalloc byte[SockAddrLayout.MaxLength];
            uint length = (uint)buffer.Length;

            using (var lease = new HandleLease(handle))
            {
                fixed (byte* sockaddr = buffer)
                    ThrowIfError(LibC.GetSockName(lease.Fd, sockaddr, &length), "getsockname");
            }

            return _info.SockAddr.Read(buffer[..(int)length]);
        }

        public SocketAddress GetRemoteAddress(SafeSocketHandle handle)
        {
            Span<byte> buffer = stackalloc byte[SockAddrLayout.MaxLength];
            uint length = (uint)buffer.Length;

            using (var lease = new HandleLease(handle))
            {
                fixed (byte* sockaddr = buffer)
                    ThrowIfError(LibC.GetPeerName(lease.Fd, sockaddr, &length), "getpeername");
            }

            return _info.SockAddr.Read(buffer[..(int)length]);
        }

        // Settings every new descriptor needs that could not be requested atomically at creation.
        private void ConfigureDescriptor(int fd)
        {
            if (_info.SockCloexec == 0 && _info.FioClex != 0)
                ThrowIfError(LibC.Ioctl(fd, _info.FioClex), "ioctl(FIOCLEX)");

            if (_info.SoNoSigPipe != 0)
                SetInt(fd, _info.SolSocket, _info.SoNoSigPipe, 1);
        }

        private void WaitForPendingConnect(int fd, int timeoutMilliseconds)
        {
            long deadline = timeoutMilliseconds < 0 ? -1 : Environment.TickCount64 + timeoutMilliseconds;
            var pollFd = new PollFd { Fd = fd, Events = PollOut };

            while (true)
            {
                int remaining = deadline < 0 ? -1 : (int)Math.Max(0, deadline - Environment.TickCount64);
                int ready = LibC.Poll(&pollFd, 1, remaining);

                if (ready > 0)
                    break;

                if (ready == 0)
                    throw CreateException(_info.ETimedOut, "connect");

                int pollError = Marshal.GetLastPInvokeError();
                if (pollError != UnixErrors.EINTR)
                    throw CreateException(pollError, "poll");
            }

            int error = 0;
            uint length = sizeof(int);
            ThrowIfError(LibC.GetSockOpt(fd, _info.SolSocket, _info.SoError, &error, &length), "getsockopt");

            if (error != 0)
                throw CreateException(error, "connect");
        }

        private void SetInt(int fd, int level, int name, int value)
            => ThrowIfError(LibC.SetSockOpt(fd, level, name, &value, sizeof(int)), "setsockopt");

        private static SafeSocketHandle Wrap(int fd)
        {
            var handle = new SafeSocketHandle();
            Marshal.InitHandle(handle, fd);
            return handle;
        }

        private void ThrowIfError(int result, string operation)
        {
            if (result == -1)
                throw LastError(operation);
        }

        private SocketException LastError(string operation) => CreateException(Marshal.GetLastPInvokeError(), operation);

        private SocketException CreateException(int errno, string operation)
        {
            // Descriptors are blocking, so EAGAIN only means SO_RCVTIMEO/SO_SNDTIMEO expired: report it as Winsock does.
            SocketError error = _info.MapErrno(errno);
            return new(error == SocketError.WouldBlock ? SocketError.TimedOut : error, errno, operation);
        }

        private static int ToNative(SocketType type) => type switch
        {
            SocketType.Stream => SockStream,
            SocketType.Datagram => SockDgram,
            _ => throw new NotSupportedException($"Socket type '{type}' is not supported.")
        };

        private static int ToNative(Protocol protocol) => protocol switch
        {
            Protocol.Unspecified => 0,
            Protocol.Tcp => IpProtoTcp,
            Protocol.Udp => IpProtoUdp,
            _ => throw new NotSupportedException($"Protocol '{protocol}' is not supported.")
        };
    }
}

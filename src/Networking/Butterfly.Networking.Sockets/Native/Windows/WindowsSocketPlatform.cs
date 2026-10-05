using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Butterfly.Networking.Sockets.Native.Windows
{
    [SupportedOSPlatform("windows")]
    internal sealed unsafe class WindowsSocketPlatform : ISocketPlatform
    {
        private const ushort WinsockVersion = 0x0202;

        public WindowsSocketPlatform()
        {
            // WSADATA is ~400 bytes and its field order differs between 32 and 64 bit; nothing in it is needed.
            byte* data = stackalloc byte[512];

            int error = Winsock.WSAStartup(WinsockVersion, data);
            if (error != 0)
                throw WindowsSocketErrors.CreateException(error, "WSAStartup");
        }

        public SafeSocketHandle Create(AddressFamily addressFamily, SocketType type, Protocol protocol)
        {
            // Same defaults as socket(), plus not leaking the handle into child processes.
            var handle = Winsock.WSASocket(
                (int)ToNative(addressFamily), (int)ToNative(type), (int)ToNative(protocol),
                protocolInfo: 0, group: 0, Winsock.WsaFlagOverlapped | Winsock.WsaFlagNoHandleInherit);

            ThrowIfInvalid(handle, "WSASocketW");

            if (type == SocketType.Datagram)
            {
                try
                {
                    DisableUdpConnReset(handle);
                }
                catch
                {
                    handle.Dispose();
                    throw;
                }
            }

            return handle;
        }

        public bool Close(nint handle) => Winsock.CloseSocket(handle) == 0;

        public void SetOption(SafeSocketHandle handle, SocketOption option, bool value)
        {
            // See SocketOptions.ReuseAddress: Windows already allows rebinding over TIME_WAIT.
            if (option == SocketOption.ReuseAddress)
                return;

            (int level, int name) = option switch
            {
                SocketOption.KeepAlive => (Winsock.SolSocket, Winsock.SoKeepAlive),
                SocketOption.NoDelay => (Winsock.IpProtoTcp, Winsock.TcpNoDelay),
                SocketOption.Broadcast => (Winsock.SolSocket, Winsock.SoBroadcast),
                _ => throw new ArgumentOutOfRangeException(nameof(option), option, null)
            };

            int optionValue = value ? 1 : 0;
            ThrowIfError(Winsock.SetSockOpt(handle, level, name, (byte*)&optionValue, sizeof(int)), "setsockopt");
        }

        public void Bind(SafeSocketHandle handle, in SocketAddress address)
        {
            Span<byte> buffer = stackalloc byte[SockAddrLayout.MaxLength];
            int length = SockAddrLayout.Windows.Write(address, buffer);

            fixed (byte* sockaddr = buffer)
                ThrowIfError(Winsock.Bind(handle, sockaddr, length), "bind");
        }

        public void Listen(SafeSocketHandle handle, int backlog)
            => ThrowIfError(Winsock.Listen(handle, backlog), "listen");

        public SafeSocketHandle Accept(SafeSocketHandle handle)
        {
            var accepted = ThrowIfInvalid(Winsock.Accept(handle, null, null), "accept");

            if (!Winsock.SetHandleInformation(accepted, Winsock.HandleFlagInherit, 0))
            {
                int error = Marshal.GetLastPInvokeError();
                accepted.Dispose();
                throw WindowsSocketErrors.CreateException(error, "SetHandleInformation");
            }

            return accepted;
        }

        public void SetTimeout(SafeSocketHandle handle, SocketTimeout kind, TimeSpan timeout)
        {
            int name = kind == SocketTimeout.Receive ? Winsock.SoRcvTimeo : Winsock.SoSndTimeo;
            int milliseconds = SocketPlatform.ToMilliseconds(timeout);
            ThrowIfError(Winsock.SetSockOpt(handle, Winsock.SolSocket, name, (byte*)&milliseconds, sizeof(int)), "setsockopt");
        }

        public void Connect(SafeSocketHandle handle, in SocketAddress address, TimeSpan timeout)
        {
            Span<byte> buffer = stackalloc byte[SockAddrLayout.MaxLength];
            int length = SockAddrLayout.Windows.Write(address, buffer);

            if (timeout == Timeout.InfiniteTimeSpan)
            {
                fixed (byte* sockaddr = buffer)
                    ThrowIfError(Winsock.Connect(handle, sockaddr, length), "connect");
                return;
            }

            // Winsock's connect ignores SO_SNDTIMEO: connect without blocking and wait with select instead.
            SetBlocking(handle, false);
            try
            {
                int result;
                fixed (byte* sockaddr = buffer)
                    result = Winsock.Connect(handle, sockaddr, length);

                if (result == Winsock.SocketErrorResult)
                {
                    int error = Marshal.GetLastPInvokeError();
                    if (error != Winsock.WsaEWouldBlock)
                        throw WindowsSocketErrors.CreateException(error, "connect");

                    WaitForPendingConnect(handle, timeout);
                }
            }
            finally
            {
                SetBlocking(handle, true);
            }
        }

        private static void WaitForPendingConnect(SafeSocketHandle handle, TimeSpan timeout)
        {
            bool added = false;
            handle.DangerousAddRef(ref added);
            try
            {
                // A successful connect shows up as writable, a failed one in the exception set.
                FdSet write = default, except = default;
                write.Count = except.Count = 1;
                write.Sockets[0] = except.Sockets[0] = handle.DangerousGetHandle();

                long microseconds = (long)SocketPlatform.ToMilliseconds(timeout) * 1000;
                var timeVal = new TimeVal { Seconds = (int)(microseconds / 1_000_000), Microseconds = (int)(microseconds % 1_000_000) };

                int ready = Winsock.Select(0, null, &write, &except, &timeVal);
                ThrowIfError(ready, "select");

                if (ready == 0)
                    throw WindowsSocketErrors.CreateException(Winsock.WsaETimedOut, "connect");

                if (except.Count > 0)
                {
                    int error = 0, size = sizeof(int);
                    ThrowIfError(Winsock.GetSockOpt(handle, Winsock.SolSocket, Winsock.SoError, (byte*)&error, &size), "getsockopt");
                    throw WindowsSocketErrors.CreateException(error, "connect");
                }
            }
            finally
            {
                if (added)
                    handle.DangerousRelease();
            }
        }

        private static void SetBlocking(SafeSocketHandle handle, bool blocking)
        {
            uint nonBlocking = blocking ? 0u : 1u;
            ThrowIfError(Winsock.IoctlSocket(handle, Winsock.FionBio, &nonBlocking), "ioctlsocket(FIONBIO)");
        }

        public int Send(SafeSocketHandle handle, ReadOnlySpan<byte> buffer)
        {
            fixed (byte* data = &MemoryMarshal.GetReference(buffer))
            {
                int sent = Winsock.Send(handle, data, buffer.Length, 0);
                ThrowIfError(sent, "send");
                return sent;
            }
        }

        public int Receive(SafeSocketHandle handle, Span<byte> buffer)
        {
            fixed (byte* data = &MemoryMarshal.GetReference(buffer))
            {
                int received = Winsock.Recv(handle, data, buffer.Length, 0);
                return received == Winsock.SocketErrorResult
                    ? TruncatedOrThrow(buffer.Length, "recv")
                    : received;
            }
        }

        public int SendTo(SafeSocketHandle handle, ReadOnlySpan<byte> buffer, in SocketAddress address)
        {
            Span<byte> addressBuffer = stackalloc byte[SockAddrLayout.MaxLength];
            int addressLength = SockAddrLayout.Windows.Write(address, addressBuffer);

            fixed (byte* data = &MemoryMarshal.GetReference(buffer))
            fixed (byte* sockaddr = addressBuffer)
            {
                int sent = Winsock.SendTo(handle, data, buffer.Length, 0, sockaddr, addressLength);
                ThrowIfError(sent, "sendto");
                return sent;
            }
        }

        public int ReceiveFrom(SafeSocketHandle handle, Span<byte> buffer, out SocketAddress address)
        {
            Span<byte> addressBuffer = stackalloc byte[SockAddrLayout.MaxLength];
            int addressLength = addressBuffer.Length;

            fixed (byte* data = &MemoryMarshal.GetReference(buffer))
            fixed (byte* sockaddr = addressBuffer)
            {
                int received = Winsock.RecvFrom(handle, data, buffer.Length, 0, sockaddr, &addressLength);
                if (received == Winsock.SocketErrorResult)
                    received = TruncatedOrThrow(buffer.Length, "recvfrom");

                address = SockAddrLayout.Windows.Read(addressBuffer[..addressLength]);
                return received;
            }
        }

        public void Shutdown(SafeSocketHandle handle, SocketShutdown how)
            => ThrowIfError(Winsock.Shutdown(handle, (int)how), "shutdown");

        // Blocking Winsock calls on an overlapped socket are overlapped I/O underneath, so CancelIoEx ends them
        // (with WSA_OPERATION_ABORTED). shutdown() alone does not wake a blocked recv on Windows.
        public void Abort(SafeSocketHandle handle) => Winsock.CancelIoEx(handle, 0);

        public SocketAddress GetLocalAddress(SafeSocketHandle handle)
        {
            Span<byte> buffer = stackalloc byte[SockAddrLayout.MaxLength];
            int length = buffer.Length;

            fixed (byte* sockaddr = buffer)
                ThrowIfError(Winsock.GetSockName(handle, sockaddr, &length), "getsockname");

            return SockAddrLayout.Windows.Read(buffer[..length]);
        }

        public SocketAddress GetRemoteAddress(SafeSocketHandle handle)
        {
            Span<byte> buffer = stackalloc byte[SockAddrLayout.MaxLength];
            int length = buffer.Length;

            fixed (byte* sockaddr = buffer)
                ThrowIfError(Winsock.GetPeerName(handle, sockaddr, &length), "getpeername");

            return SockAddrLayout.Windows.Read(buffer[..length]);
        }

        // By default an ICMP "port unreachable" for an earlier sendto makes the next recvfrom fail with WSAECONNRESET,
        // even on an unconnected socket. Unix does not report these on unconnected sockets, so turn it off.
        private static void DisableUdpConnReset(SafeSocketHandle handle)
        {
            int enabled = 0;
            uint bytesReturned;

            ThrowIfError(Winsock.WSAIoctl(handle, Winsock.SioUdpConnReset, &enabled, sizeof(int), null, 0, &bytesReturned, 0, 0), "WSAIoctl(SIO_UDP_CONNRESET)");
        }

        // Winsock fails an oversized datagram after filling the buffer; Unix truncates it silently. Match Unix.
        private static int TruncatedOrThrow(int bufferLength, string operation)
        {
            int error = Marshal.GetLastPInvokeError();
            if (error != Winsock.WsaEMsgSize)
                throw WindowsSocketErrors.CreateException(error, operation);

            return bufferLength;
        }

        private static void ThrowIfError(int result, string operation)
        {
            if (result == Winsock.SocketErrorResult)
                throw WindowsSocketErrors.CreateException(Marshal.GetLastPInvokeError(), operation);
        }

        private static SafeSocketHandle ThrowIfInvalid(SafeSocketHandle handle, string operation)
        {
            if (!handle.IsInvalid)
                return handle;

            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw WindowsSocketErrors.CreateException(error, operation);
        }

        private static WindowsAddressFamily ToNative(AddressFamily addressFamily) => addressFamily switch
        {
            AddressFamily.IPv4 => WindowsAddressFamily.IPv4,
            AddressFamily.IPv6 => WindowsAddressFamily.IPv6,
            _ => throw new NotSupportedException($"Address family '{addressFamily}' is not supported.")
        };

        private static WindowsSocketType ToNative(SocketType type) => type switch
        {
            SocketType.Stream => WindowsSocketType.Stream,
            SocketType.Datagram => WindowsSocketType.Datagram,
            _ => throw new NotSupportedException($"Socket type '{type}' is not supported.")
        };

        private static WindowsProtocol ToNative(Protocol protocol) => protocol switch
        {
            Protocol.Unspecified => WindowsProtocol.Unspecified,
            Protocol.Tcp => WindowsProtocol.Tcp,
            Protocol.Udp => WindowsProtocol.Udp,
            _ => throw new NotSupportedException($"Protocol '{protocol}' is not supported.")
        };
    }
}

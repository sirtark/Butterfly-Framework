using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Butterfly.Networking.Sockets.Native.Windows
{
    [SupportedOSPlatform("windows")]
    internal static unsafe partial class Winsock
    {
        private const string Library = "Ws2_32.dll";
        private const string Kernel32 = "kernel32.dll";

        public const int SocketErrorResult = -1;

        public const int SolSocket = 0xFFFF;
        public const int SoKeepAlive = 0x0008;
        public const int SoBroadcast = 0x0020;
        public const int SoSndTimeo = 0x1005;
        public const int SoRcvTimeo = 0x1006;
        public const int SoError = 0x1007;
        public const int FionBio = unchecked((int)0x8004667E);
        public const int IpProtoTcp = 6;
        public const int TcpNoDelay = 0x0001;

        public const uint WsaFlagOverlapped = 0x01;
        public const uint WsaFlagNoHandleInherit = 0x80;
        public const uint HandleFlagInherit = 0x01;
        public const uint SioUdpConnReset = 0x9800000C;

        public const int WsaEWouldBlock = 10035;
        public const int WsaEMsgSize = 10040;
        public const int WsaETimedOut = 10060;

        [LibraryImport(Library, EntryPoint = "getsockopt", SetLastError = true)]
        public static partial int GetSockOpt(SafeSocketHandle socket, int level, int optionName, byte* optionValue, int* optionLength);

        [LibraryImport(Library, EntryPoint = "ioctlsocket", SetLastError = true)]
        public static partial int IoctlSocket(SafeSocketHandle socket, int command, uint* argument);

        // The first parameter is ignored by Winsock; it exists for Berkeley compatibility.
        [LibraryImport(Library, EntryPoint = "select", SetLastError = true)]
        public static partial int Select(int ignored, FdSet* read, FdSet* write, FdSet* except, TimeVal* timeout);

        [LibraryImport(Library, EntryPoint = "WSAStartup")]
        public static partial int WSAStartup(ushort versionRequested, byte* data);

        [LibraryImport(Library, EntryPoint = "WSASocketW", SetLastError = true)]
        public static partial SafeSocketHandle WSASocket(int addressFamily, int type, int protocol, nint protocolInfo, uint group, uint flags);

        [LibraryImport(Kernel32, EntryPoint = "SetHandleInformation", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool SetHandleInformation(SafeSocketHandle handle, uint mask, uint flags);

        [LibraryImport(Kernel32, EntryPoint = "CancelIoEx", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool CancelIoEx(SafeSocketHandle handle, nint overlapped);

        [LibraryImport(Library, EntryPoint = "closesocket", SetLastError = true)]
        public static partial int CloseSocket(nint socket);

        [LibraryImport(Library, EntryPoint = "setsockopt", SetLastError = true)]
        public static partial int SetSockOpt(SafeSocketHandle socket, int level, int optionName, byte* optionValue, int optionLength);

        [LibraryImport(Library, EntryPoint = "bind", SetLastError = true)]
        public static partial int Bind(SafeSocketHandle socket, byte* address, int addressLength);

        [LibraryImport(Library, EntryPoint = "listen", SetLastError = true)]
        public static partial int Listen(SafeSocketHandle socket, int backlog);

        [LibraryImport(Library, EntryPoint = "accept", SetLastError = true)]
        public static partial SafeSocketHandle Accept(SafeSocketHandle socket, byte* address, int* addressLength);

        [LibraryImport(Library, EntryPoint = "connect", SetLastError = true)]
        public static partial int Connect(SafeSocketHandle socket, byte* address, int addressLength);

        [LibraryImport(Library, EntryPoint = "send", SetLastError = true)]
        public static partial int Send(SafeSocketHandle socket, byte* buffer, int length, int flags);

        [LibraryImport(Library, EntryPoint = "recv", SetLastError = true)]
        public static partial int Recv(SafeSocketHandle socket, byte* buffer, int length, int flags);

        [LibraryImport(Library, EntryPoint = "sendto", SetLastError = true)]
        public static partial int SendTo(SafeSocketHandle socket, byte* buffer, int length, int flags, byte* address, int addressLength);

        [LibraryImport(Library, EntryPoint = "recvfrom", SetLastError = true)]
        public static partial int RecvFrom(SafeSocketHandle socket, byte* buffer, int length, int flags, byte* address, int* addressLength);

        [LibraryImport(Library, EntryPoint = "WSAIoctl", SetLastError = true)]
        public static partial int WSAIoctl(SafeSocketHandle socket, uint controlCode, void* inBuffer, uint inLength,
            void* outBuffer, uint outLength, uint* bytesReturned, nint overlapped, nint completionRoutine);

        [LibraryImport(Library, EntryPoint = "shutdown", SetLastError = true)]
        public static partial int Shutdown(SafeSocketHandle socket, int how);

        [LibraryImport(Library, EntryPoint = "getsockname", SetLastError = true)]
        public static partial int GetSockName(SafeSocketHandle socket, byte* address, int* addressLength);

        [LibraryImport(Library, EntryPoint = "getpeername", SetLastError = true)]
        public static partial int GetPeerName(SafeSocketHandle socket, byte* address, int* addressLength);
    }

    // fd_set: a count followed by FD_SETSIZE (64) SOCKET values.
    [StructLayout(LayoutKind.Sequential)]
    internal struct FdSet
    {
        public uint Count;
        public FdArray Sockets;

        [System.Runtime.CompilerServices.InlineArray(64)]
        public struct FdArray
        {
            private nint _element0;
        }
    }

    // struct timeval with Windows' 32-bit longs.
    [StructLayout(LayoutKind.Sequential)]
    internal struct TimeVal
    {
        public int Seconds;
        public int Microseconds;
    }
}

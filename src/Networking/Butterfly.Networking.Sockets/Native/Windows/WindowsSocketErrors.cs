namespace Butterfly.Networking.Sockets.Native.Windows
{
    internal static class WindowsSocketErrors
    {
        public static SocketException CreateException(int nativeError, string operation)
            => new(ToSocketError(nativeError), nativeError, operation);

        public static SocketError ToSocketError(int nativeError) => nativeError switch
        {
            0 => SocketError.Success,
            995 => SocketError.OperationAborted,            // WSA_OPERATION_ABORTED (CancelIoEx)
            10004 => SocketError.Interrupted,               // WSAEINTR
            10009 => SocketError.NotSocket,                 // WSAEBADF
            10013 => SocketError.AccessDenied,              // WSAEACCES
            10014 => SocketError.InvalidArgument,           // WSAEFAULT
            10022 => SocketError.InvalidArgument,           // WSAEINVAL
            10024 => SocketError.TooManyOpenSockets,        // WSAEMFILE
            10035 => SocketError.WouldBlock,                // WSAEWOULDBLOCK
            10036 => SocketError.InProgress,                // WSAEINPROGRESS
            10037 => SocketError.AlreadyInProgress,         // WSAEALREADY
            10038 => SocketError.NotSocket,                 // WSAENOTSOCK
            10039 => SocketError.DestinationAddressRequired,// WSAEDESTADDRREQ
            10040 => SocketError.MessageSize,               // WSAEMSGSIZE
            10041 => SocketError.ProtocolType,              // WSAEPROTOTYPE
            10042 => SocketError.ProtocolOption,            // WSAENOPROTOOPT
            10043 => SocketError.ProtocolNotSupported,      // WSAEPROTONOSUPPORT
            10044 => SocketError.SocketNotSupported,        // WSAESOCKTNOSUPPORT
            10045 => SocketError.OperationNotSupported,     // WSAEOPNOTSUPP
            10046 => SocketError.ProtocolFamilyNotSupported,// WSAEPFNOSUPPORT
            10047 => SocketError.AddressFamilyNotSupported, // WSAEAFNOSUPPORT
            10048 => SocketError.AddressInUse,              // WSAEADDRINUSE
            10049 => SocketError.AddressNotAvailable,       // WSAEADDRNOTAVAIL
            10050 => SocketError.NetworkDown,               // WSAENETDOWN
            10051 => SocketError.NetworkUnreachable,        // WSAENETUNREACH
            10052 => SocketError.NetworkReset,              // WSAENETRESET
            10053 => SocketError.ConnectionAborted,         // WSAECONNABORTED
            10054 => SocketError.ConnectionReset,           // WSAECONNRESET
            10055 => SocketError.NoBufferSpace,             // WSAENOBUFS
            10056 => SocketError.IsConnected,               // WSAEISCONN
            10057 => SocketError.NotConnected,              // WSAENOTCONN
            10058 => SocketError.Shutdown,                  // WSAESHUTDOWN
            10060 => SocketError.TimedOut,                  // WSAETIMEDOUT
            10061 => SocketError.ConnectionRefused,         // WSAECONNREFUSED
            10064 => SocketError.HostDown,                  // WSAEHOSTDOWN
            10065 => SocketError.HostUnreachable,           // WSAEHOSTUNREACH
            10091 => SocketError.SystemNotReady,            // WSASYSNOTREADY
            10092 => SocketError.VersionNotSupported,       // WSAVERNOTSUPPORTED
            10093 => SocketError.NotInitialized,            // WSANOTINITIALISED
            _ => SocketError.Unknown
        };
    }
}

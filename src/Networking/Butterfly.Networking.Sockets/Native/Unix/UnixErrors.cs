namespace Butterfly.Networking.Sockets.Native.Unix
{
    internal static class UnixErrors
    {
        public const int EINTR = 4;

        public static SocketError FromLinux(int errno) => errno switch
        {
            11 => SocketError.WouldBlock,                 // EAGAIN
            88 => SocketError.NotSocket,                  // ENOTSOCK
            89 => SocketError.DestinationAddressRequired, // EDESTADDRREQ
            90 => SocketError.MessageSize,                // EMSGSIZE
            91 => SocketError.ProtocolType,               // EPROTOTYPE
            92 => SocketError.ProtocolOption,             // ENOPROTOOPT
            93 => SocketError.ProtocolNotSupported,       // EPROTONOSUPPORT
            94 => SocketError.SocketNotSupported,         // ESOCKTNOSUPPORT
            95 => SocketError.OperationNotSupported,      // EOPNOTSUPP
            96 => SocketError.ProtocolFamilyNotSupported, // EPFNOSUPPORT
            97 => SocketError.AddressFamilyNotSupported,  // EAFNOSUPPORT
            98 => SocketError.AddressInUse,               // EADDRINUSE
            99 => SocketError.AddressNotAvailable,        // EADDRNOTAVAIL
            100 => SocketError.NetworkDown,               // ENETDOWN
            101 => SocketError.NetworkUnreachable,        // ENETUNREACH
            102 => SocketError.NetworkReset,              // ENETRESET
            103 => SocketError.ConnectionAborted,         // ECONNABORTED
            104 => SocketError.ConnectionReset,           // ECONNRESET
            105 => SocketError.NoBufferSpace,             // ENOBUFS
            106 => SocketError.IsConnected,               // EISCONN
            107 => SocketError.NotConnected,              // ENOTCONN
            108 => SocketError.Shutdown,                  // ESHUTDOWN
            110 => SocketError.TimedOut,                  // ETIMEDOUT
            111 => SocketError.ConnectionRefused,         // ECONNREFUSED
            112 => SocketError.HostDown,                  // EHOSTDOWN
            113 => SocketError.HostUnreachable,           // EHOSTUNREACH
            114 => SocketError.AlreadyInProgress,         // EALREADY
            115 => SocketError.InProgress,                // EINPROGRESS
            _ => FromCommon(errno)
        };

        // FreeBSD and Apple. Winsock copied these numbers, which is why they are the WSAE* codes minus 10000.
        public static SocketError FromBsd(int errno) => errno switch
        {
            35 => SocketError.WouldBlock,                 // EAGAIN
            36 => SocketError.InProgress,                 // EINPROGRESS
            37 => SocketError.AlreadyInProgress,          // EALREADY
            38 => SocketError.NotSocket,                  // ENOTSOCK
            39 => SocketError.DestinationAddressRequired, // EDESTADDRREQ
            40 => SocketError.MessageSize,                // EMSGSIZE
            41 => SocketError.ProtocolType,               // EPROTOTYPE
            42 => SocketError.ProtocolOption,             // ENOPROTOOPT
            43 => SocketError.ProtocolNotSupported,       // EPROTONOSUPPORT
            44 => SocketError.SocketNotSupported,         // ESOCKTNOSUPPORT
            45 => SocketError.OperationNotSupported,      // EOPNOTSUPP (FreeBSD), ENOTSUP (Apple)
            46 => SocketError.ProtocolFamilyNotSupported, // EPFNOSUPPORT
            47 => SocketError.AddressFamilyNotSupported,  // EAFNOSUPPORT
            48 => SocketError.AddressInUse,               // EADDRINUSE
            49 => SocketError.AddressNotAvailable,        // EADDRNOTAVAIL
            50 => SocketError.NetworkDown,                // ENETDOWN
            51 => SocketError.NetworkUnreachable,         // ENETUNREACH
            52 => SocketError.NetworkReset,               // ENETRESET
            53 => SocketError.ConnectionAborted,          // ECONNABORTED
            54 => SocketError.ConnectionReset,            // ECONNRESET
            55 => SocketError.NoBufferSpace,              // ENOBUFS
            56 => SocketError.IsConnected,                // EISCONN
            57 => SocketError.NotConnected,               // ENOTCONN
            58 => SocketError.Shutdown,                   // ESHUTDOWN
            60 => SocketError.TimedOut,                   // ETIMEDOUT
            61 => SocketError.ConnectionRefused,          // ECONNREFUSED
            64 => SocketError.HostDown,                   // EHOSTDOWN
            65 => SocketError.HostUnreachable,            // EHOSTUNREACH
            102 => SocketError.OperationNotSupported,     // EOPNOTSUPP (Apple)
            _ => FromCommon(errno)
        };

        private static SocketError FromCommon(int errno) => errno switch
        {
            0 => SocketError.Success,
            EINTR => SocketError.Interrupted,
            9 => SocketError.NotSocket,                   // EBADF
            13 => SocketError.AccessDenied,               // EACCES
            14 => SocketError.InvalidArgument,            // EFAULT
            22 => SocketError.InvalidArgument,            // EINVAL
            24 => SocketError.TooManyOpenSockets,         // EMFILE
            32 => SocketError.Shutdown,                   // EPIPE
            _ => SocketError.Unknown
        };
    }
}

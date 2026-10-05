namespace Butterfly.Networking.Sockets.Native.Unix
{
    // The constants that differ between Unix flavors. Anything identical everywhere lives in UnixSocketPlatform.
    internal sealed class UnixPlatformInfo
    {
        public required string Name { get; init; }
        public required SockAddrLayout SockAddr { get; init; }
        public required Func<int, SocketError> MapErrno { get; init; }

        public required int SolSocket { get; init; }
        public required int SoReuseAddr { get; init; }
        public required int SoKeepAlive { get; init; }
        public required int SoBroadcast { get; init; }
        public required int SoError { get; init; }
        public required int SoRcvTimeo { get; init; }
        public required int SoSndTimeo { get; init; }
        public required int Ipv6V6Only { get; init; }
        public required int ONonBlock { get; init; }
        public required int EInProgress { get; init; }
        public required int ETimedOut { get; init; }

        /// <summary>OR-ed into socket()/accept4() types; 0 when the OS has no SOCK_CLOEXEC.</summary>
        public int SockCloexec { get; init; }

        /// <summary>ioctl request that sets close-on-exec; used when <see cref="SockCloexec"/> is unavailable.</summary>
        public nuint FioClex { get; init; }

        /// <summary>send() flag that suppresses SIGPIPE; 0 when the OS uses <see cref="SoNoSigPipe"/> instead.</summary>
        public int MsgNoSignal { get; init; }

        /// <summary>Socket option that suppresses SIGPIPE; 0 when unavailable.</summary>
        public int SoNoSigPipe { get; init; }

        public bool HasAccept4 { get; init; }

        // Linux, including Android (bionic uses the Linux values).
        public static UnixPlatformInfo Linux { get; } = new()
        {
            Name = "Linux",
            SockAddr = SockAddrLayout.Linux,
            MapErrno = UnixErrors.FromLinux,
            SolSocket = 1,
            SoReuseAddr = 2,
            SoKeepAlive = 9,
            SoBroadcast = 6,
            SoError = 4,
            SoRcvTimeo = 20,
            SoSndTimeo = 21,
            Ipv6V6Only = 26,
            ONonBlock = 0x800,
            EInProgress = 115,
            ETimedOut = 110,
            SockCloexec = 0x80000,
            MsgNoSignal = 0x4000,
            HasAccept4 = true
        };

        // macOS, iOS, tvOS, watchOS and Mac Catalyst.
        public static UnixPlatformInfo Apple { get; } = new()
        {
            Name = "Apple",
            SockAddr = SockAddrLayout.Apple,
            MapErrno = UnixErrors.FromBsd,
            SolSocket = 0xFFFF,
            SoReuseAddr = 0x0004,
            SoKeepAlive = 0x0008,
            SoBroadcast = 0x0020,
            SoError = 0x1007,
            SoRcvTimeo = 0x1006,
            SoSndTimeo = 0x1005,
            Ipv6V6Only = 27,
            ONonBlock = 0x4,
            EInProgress = 36,
            ETimedOut = 60,
            FioClex = 0x20006601,
            SoNoSigPipe = 0x1022
        };

        public static UnixPlatformInfo FreeBSD { get; } = new()
        {
            Name = "FreeBSD",
            SockAddr = SockAddrLayout.FreeBSD,
            MapErrno = UnixErrors.FromBsd,
            SolSocket = 0xFFFF,
            SoReuseAddr = 0x0004,
            SoKeepAlive = 0x0008,
            SoBroadcast = 0x0020,
            SoError = 0x1007,
            SoRcvTimeo = 0x1006,
            SoSndTimeo = 0x1005,
            Ipv6V6Only = 27,
            ONonBlock = 0x4,
            EInProgress = 36,
            ETimedOut = 60,
            SockCloexec = 0x10000000,
            MsgNoSignal = 0x20000,
            HasAccept4 = true
        };
    }
}

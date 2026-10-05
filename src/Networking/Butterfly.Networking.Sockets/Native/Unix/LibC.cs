using System.Reflection;
using System.Runtime.InteropServices;

namespace Butterfly.Networking.Sockets.Native.Unix
{
    // struct timeval as used by SO_RCVTIMEO/SO_SNDTIMEO: two C longs (Apple's int tv_usec is padded to the same size).
    [StructLayout(LayoutKind.Sequential)]
    internal struct TimeVal
    {
        public nint Seconds;
        public nint Microseconds;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    // File descriptors are passed as int (not SafeHandle, which marshals as a pointer-sized value);
    // callers keep the SafeHandle alive with HandleLease.
    internal static unsafe partial class LibC
    {
        public const string Name = "libc";

        [LibraryImport(Name, EntryPoint = "socket", SetLastError = true)]
        public static partial int Socket(int domain, int type, int protocol);

        [LibraryImport(Name, EntryPoint = "close", SetLastError = true)]
        public static partial int Close(int fd);

        [LibraryImport(Name, EntryPoint = "ioctl", SetLastError = true)]
        public static partial int Ioctl(int fd, nuint request);

        [LibraryImport(Name, EntryPoint = "setsockopt", SetLastError = true)]
        public static partial int SetSockOpt(int fd, int level, int optionName, void* optionValue, uint optionLength);

        [LibraryImport(Name, EntryPoint = "getsockopt", SetLastError = true)]
        public static partial int GetSockOpt(int fd, int level, int optionName, void* optionValue, uint* optionLength);

        [LibraryImport(Name, EntryPoint = "bind", SetLastError = true)]
        public static partial int Bind(int fd, byte* address, uint addressLength);

        [LibraryImport(Name, EntryPoint = "listen", SetLastError = true)]
        public static partial int Listen(int fd, int backlog);

        [LibraryImport(Name, EntryPoint = "accept", SetLastError = true)]
        public static partial int Accept(int fd, byte* address, uint* addressLength);

        [LibraryImport(Name, EntryPoint = "accept4", SetLastError = true)]
        public static partial int Accept4(int fd, byte* address, uint* addressLength, int flags);

        [LibraryImport(Name, EntryPoint = "connect", SetLastError = true)]
        public static partial int Connect(int fd, byte* address, uint addressLength);

        [LibraryImport(Name, EntryPoint = "send", SetLastError = true)]
        public static partial nint Send(int fd, byte* buffer, nuint length, int flags);

        [LibraryImport(Name, EntryPoint = "recv", SetLastError = true)]
        public static partial nint Recv(int fd, byte* buffer, nuint length, int flags);

        [LibraryImport(Name, EntryPoint = "sendto", SetLastError = true)]
        public static partial nint SendTo(int fd, byte* buffer, nuint length, int flags, byte* address, uint addressLength);

        [LibraryImport(Name, EntryPoint = "recvfrom", SetLastError = true)]
        public static partial nint RecvFrom(int fd, byte* buffer, nuint length, int flags, byte* address, uint* addressLength);

        [LibraryImport(Name, EntryPoint = "shutdown", SetLastError = true)]
        public static partial int Shutdown(int fd, int how);

        [LibraryImport(Name, EntryPoint = "getsockname", SetLastError = true)]
        public static partial int GetSockName(int fd, byte* address, uint* addressLength);

        [LibraryImport(Name, EntryPoint = "getpeername", SetLastError = true)]
        public static partial int GetPeerName(int fd, byte* address, uint* addressLength);

        [LibraryImport(Name, EntryPoint = "poll", SetLastError = true)]
        public static partial int Poll(PollFd* fds, nuint count, int timeout);

        public const int FGetFl = 3;
        public const int FSetFl = 4;

        [LibraryImport(Name, EntryPoint = "fcntl", SetLastError = true)]
        public static partial int FcntlGet(int fd, int command);

        // fcntl is variadic. Everywhere but Apple arm64 a variadic int travels like a named one.
        [LibraryImport(Name, EntryPoint = "fcntl", SetLastError = true)]
        private static partial int FcntlRegister(int fd, int command, int argument);

        // Apple arm64 passes variadic arguments on the stack: fill the 8 argument registers so the value lands there.
        [LibraryImport(Name, EntryPoint = "fcntl", SetLastError = true)]
        private static partial int FcntlStack(int fd, int command, nint x2, nint x3, nint x4, nint x5, nint x6, nint x7, nint argument);

        private static readonly bool s_variadicArgumentsOnStack =
            RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            && (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() || OperatingSystem.IsTvOS()
                || OperatingSystem.IsWatchOS() || OperatingSystem.IsMacCatalyst());

        public static int FcntlSet(int fd, int command, int argument)
            => s_variadicArgumentsOnStack
                ? FcntlStack(fd, command, 0, 0, 0, 0, 0, 0, argument)
                : FcntlRegister(fd, command, argument);

        private static int s_resolverRegistered;

        // "libc" is not a real file name on most systems (on glibc, libc.so is a linker script), and NativeAOT
        // does not special-case it the way the CoreCLR loader does, so it is resolved explicitly.
        public static void RegisterResolver()
        {
            if (Interlocked.Exchange(ref s_resolverRegistered, 1) == 0)
                NativeLibrary.SetDllImportResolver(typeof(LibC).Assembly, Resolve);
        }

        private static nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (libraryName != Name)
                return 0;

            // libc is always loaded already, so the process' global symbol scope has everything.
            nint program = NativeLibrary.GetMainProgramHandle();
            if (NativeLibrary.TryGetExport(program, "socket", out _))
                return program;

            string[] candidates = ["libc.so.6", "libc.so.7", "libc.so", "/usr/lib/libSystem.B.dylib"];
            foreach (string candidate in candidates)
            {
                if (NativeLibrary.TryLoad(candidate, out nint library))
                    return library;
            }

            return 0;
        }
    }

    internal readonly ref struct HandleLease
    {
        private readonly SafeSocketHandle _handle;

        public HandleLease(SafeSocketHandle handle)
        {
            bool added = false;
            handle.DangerousAddRef(ref added);

            _handle = handle;
            Fd = (int)handle.DangerousGetHandle();
        }

        public int Fd { get; }

        public void Dispose() => _handle.DangerousRelease();
    }
}

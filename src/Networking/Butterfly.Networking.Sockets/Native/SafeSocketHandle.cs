using Microsoft.Win32.SafeHandles;

namespace Butterfly.Networking.Sockets.Native
{
    // -1 is INVALID_SOCKET on Windows and the error value on POSIX; 0 is a valid descriptor on POSIX.
    public sealed class SafeSocketHandle : SafeHandleMinusOneIsInvalid
    {
        public SafeSocketHandle() : base(ownsHandle: true)
        { }

        public override string ToString() => $"0x{handle.ToInt64():X}";

        protected override bool ReleaseHandle() => SocketPlatform.Current.Close(handle);
    }
}

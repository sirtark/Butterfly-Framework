namespace Butterfly.Networking.Sockets.Native.Windows
{
    internal enum WindowsAddressFamily : ushort
    {
        Unspecified = 0,
        IPv4 = 2,
        IPv6 = 23
    }

    internal enum WindowsSocketType
    {
        Stream = 1,
        Datagram = 2,
        Raw = 3
    }

    internal enum WindowsProtocol
    {
        Unspecified = 0,
        Tcp = 6,
        Udp = 17
    }
}

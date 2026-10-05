using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Butterfly.Networking.Sockets.Native
{
    // sockaddr_in / sockaddr_in6. Every supported OS uses the same offsets; they differ only in the value of
    // AF_INET6 and in how the family is stored: a host-order ushort, or (BSD, Apple) a length byte plus a family byte.
    internal sealed class SockAddrLayout(ushort afInet, ushort afInet6, bool hasLengthField)
    {
        public const int MaxLength = 128; // sizeof(sockaddr_storage)

        private const int IPv4Length = 16; // sizeof(sockaddr_in)
        private const int IPv6Length = 28; // sizeof(sockaddr_in6)

        public static readonly SockAddrLayout Windows = new(2, 23, hasLengthField: false);
        public static readonly SockAddrLayout Linux = new(2, 10, hasLengthField: false);
        public static readonly SockAddrLayout Apple = new(2, 30, hasLengthField: true);
        public static readonly SockAddrLayout FreeBSD = new(2, 28, hasLengthField: true);

        public ushort AfInet { get; } = afInet;
        public ushort AfInet6 { get; } = afInet6;

        public int Write(in SocketAddress address, Span<byte> destination)
        {
            switch (address.Family)
            {
                case AddressFamily.IPv4:
                    destination[..IPv4Length].Clear();
                    WriteFamily(destination, AfInet, IPv4Length);
                    BinaryPrimitives.WriteUInt16BigEndian(destination[2..], address.Port);
                    address.CopyAddressTo(destination[4..]);
                    return IPv4Length;

                case AddressFamily.IPv6:
                    destination[..IPv6Length].Clear();
                    WriteFamily(destination, AfInet6, IPv6Length);
                    BinaryPrimitives.WriteUInt16BigEndian(destination[2..], address.Port);
                    address.CopyAddressTo(destination[8..]);
                    MemoryMarshal.Write(destination[24..], address.ScopeId);
                    return IPv6Length;

                default:
                    throw new ArgumentException($"Cannot convert an address of family '{address.Family}' to a sockaddr.", nameof(address));
            }
        }

        public SocketAddress Read(ReadOnlySpan<byte> source)
        {
            if (source.Length < 4)
                throw new NotSupportedException($"A sockaddr of {source.Length} bytes is too short.");

            ushort family = hasLengthField ? source[1] : MemoryMarshal.Read<ushort>(source);
            ushort port = BinaryPrimitives.ReadUInt16BigEndian(source[2..]);

            if (family == AfInet && source.Length >= IPv4Length)
                return new SocketAddress(source.Slice(4, SocketAddress.IPv4AddressLength), port);

            if (family == AfInet6 && source.Length >= IPv6Length)
                return new SocketAddress(source.Slice(8, SocketAddress.IPv6AddressLength), port, MemoryMarshal.Read<uint>(source[24..]));

            throw new NotSupportedException($"Unsupported sockaddr family {family} ({source.Length} bytes).");
        }

        private void WriteFamily(Span<byte> destination, ushort family, int length)
        {
            if (hasLengthField)
            {
                destination[0] = (byte)length;
                destination[1] = (byte)family;
            }
            else
            {
                MemoryMarshal.Write(destination, family);
            }
        }
    }
}

using System.Runtime.CompilerServices;

namespace Butterfly.Networking.Sockets
{
    public readonly struct SocketAddress : IEquatable<SocketAddress>
    {
        public const int IPv4AddressLength = 4;
        public const int IPv6AddressLength = 16;

        private readonly AddressBuffer _address;

        public SocketAddress(ReadOnlySpan<byte> address, ushort port, uint scopeId = 0)
        {
            Family = address.Length switch
            {
                IPv4AddressLength => AddressFamily.IPv4,
                IPv6AddressLength => AddressFamily.IPv6,
                _ => throw new ArgumentException(
                    $"An address must be {IPv4AddressLength} (IPv4) or {IPv6AddressLength} (IPv6) bytes long.", nameof(address))
            };

            if (scopeId != 0 && Family != AddressFamily.IPv6)
                throw new ArgumentException("Only IPv6 addresses can have a scope id.", nameof(scopeId));

            address.CopyTo(_address);
            Port = port;
            ScopeId = scopeId;
        }

        public AddressFamily Family { get; }
        public ushort Port { get; }
        public uint ScopeId { get; }

        public int AddressLength => Family switch
        {
            AddressFamily.IPv4 => IPv4AddressLength,
            AddressFamily.IPv6 => IPv6AddressLength,
            _ => 0
        };

        public static SocketAddress Any(AddressFamily family, ushort port) => family switch
        {
            AddressFamily.IPv4 => new([0, 0, 0, 0], port),
            AddressFamily.IPv6 => new([0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], port),
            _ => throw new ArgumentOutOfRangeException(nameof(family), family, "Expected IPv4 or IPv6.")
        };

        public static SocketAddress Loopback(AddressFamily family, ushort port) => family switch
        {
            AddressFamily.IPv4 => new([127, 0, 0, 1], port),
            AddressFamily.IPv6 => new([0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1], port),
            _ => throw new ArgumentOutOfRangeException(nameof(family), family, "Expected IPv4 or IPv6.")
        };

        public static SocketAddress Parse(string address, ushort port)
        {
            ArgumentNullException.ThrowIfNull(address);

            return TryParse(address, port, out var result)
                ? result
                : throw new FormatException($"'{address}' is not a valid IPv4 or IPv6 address.");
        }

        public static bool TryParse(ReadOnlySpan<char> address, ushort port, out SocketAddress result)
        {
            Span<byte> bytes = stackalloc byte[IPv6AddressLength];

            if (IPAddressText.TryParseIPv4(address, bytes))
            {
                result = new SocketAddress(bytes[..IPv4AddressLength], port);
                return true;
            }

            if (address.Length >= 2 && address[0] == '[' && address[^1] == ']')
                address = address[1..^1];

            if (IPAddressText.TryParseIPv6(address, bytes, out uint scopeId))
            {
                result = new SocketAddress(bytes, port, scopeId);
                return true;
            }

            result = default;
            return false;
        }

        public int CopyAddressTo(Span<byte> destination)
        {
            ReadOnlySpan<byte> address = _address;
            address[..AddressLength].CopyTo(destination);
            return AddressLength;
        }

        public byte[] GetAddressBytes()
        {
            ReadOnlySpan<byte> address = _address;
            return address[..AddressLength].ToArray();
        }

        public string AddressToString()
        {
            ReadOnlySpan<byte> address = _address;

            return Family switch
            {
                AddressFamily.IPv4 => IPAddressText.FormatIPv4(address),
                AddressFamily.IPv6 => IPAddressText.FormatIPv6(address, ScopeId),
                _ => string.Empty
            };
        }

        public override string ToString() => Family switch
        {
            AddressFamily.IPv4 => $"{AddressToString()}:{Port}",
            AddressFamily.IPv6 => $"[{AddressToString()}]:{Port}",
            _ => "(unspecified)"
        };

        public bool Equals(SocketAddress other)
        {
            ReadOnlySpan<byte> address = _address;
            ReadOnlySpan<byte> otherAddress = other._address;

            return Family == other.Family
                && Port == other.Port
                && ScopeId == other.ScopeId
                && address.SequenceEqual(otherAddress);
        }

        public override bool Equals(object? obj) => obj is SocketAddress other && Equals(other);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(Family);
            hash.Add(Port);
            hash.Add(ScopeId);
            hash.AddBytes(_address);
            return hash.ToHashCode();
        }

        public static bool operator ==(SocketAddress left, SocketAddress right) => left.Equals(right);
        public static bool operator !=(SocketAddress left, SocketAddress right) => !left.Equals(right);

        [InlineArray(IPv6AddressLength)]
        private struct AddressBuffer
        {
            private byte _element0;
        }
    }
}

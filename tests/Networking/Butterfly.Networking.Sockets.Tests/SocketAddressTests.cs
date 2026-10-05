namespace Butterfly.Networking.Sockets.Tests
{
    public class SocketAddressTests
    {
        [Theory]
        [InlineData("127.0.0.1", new byte[] { 127, 0, 0, 1 })]
        [InlineData("0.0.0.0", new byte[] { 0, 0, 0, 0 })]
        [InlineData("255.255.255.255", new byte[] { 255, 255, 255, 255 })]
        [InlineData("10.0.20.3", new byte[] { 10, 0, 20, 3 })]
        public void ParsesIPv4(string text, byte[] expected)
        {
            var address = SocketAddress.Parse(text, 80);

            Assert.Equal(AddressFamily.IPv4, address.Family);
            Assert.Equal(expected, address.GetAddressBytes());
            Assert.Equal((ushort)80, address.Port);
        }

        [Theory]
        [InlineData("::", "::")]
        [InlineData("::1", "::1")]
        [InlineData("1::", "1::")]
        [InlineData("2001:DB8::1", "2001:db8::1")]
        [InlineData("2001:db8:0:0:1:0:0:1", "2001:db8::1:0:0:1")]
        [InlineData("2001:db8:0:1:1:1:1:1", "2001:db8:0:1:1:1:1:1")]
        [InlineData("fe80::1%12", "fe80::1%12")]
        [InlineData("[::1]", "::1")]
        [InlineData("::ffff:192.168.1.1", "::ffff:192.168.1.1")]
        [InlineData("1:2:3:4:5:6:7:8", "1:2:3:4:5:6:7:8")]
        public void ParsesAndFormatsIPv6(string text, string expected)
        {
            var address = SocketAddress.Parse(text, 443);

            Assert.Equal(AddressFamily.IPv6, address.Family);
            Assert.Equal(expected, address.AddressToString());
        }

        [Theory]
        [InlineData("")]
        [InlineData("1.2.3")]
        [InlineData("1.2.3.4.5")]
        [InlineData("256.0.0.1")]
        [InlineData("01.2.3.4")]
        [InlineData("1.2.3.")]
        [InlineData(":1")]
        [InlineData("1:2")]
        [InlineData("1:::2")]
        [InlineData("1::2::3")]
        [InlineData("1:2:3:4:5:6:7:8:9")]
        [InlineData("12345::")]
        [InlineData("fe80::1%x")]
        [InlineData("localhost")]
        public void RejectsInvalidAddresses(string text)
        {
            Assert.False(SocketAddress.TryParse(text, 0, out _));
            Assert.Throws<FormatException>(() => SocketAddress.Parse(text, 0));
        }

        [Fact]
        public void ScopeIdIsParsed()
        {
            Assert.Equal(12u, SocketAddress.Parse("fe80::1%12", 0).ScopeId);
        }

        [Fact]
        public void ToStringIncludesPort()
        {
            Assert.Equal("127.0.0.1:8080", SocketAddress.Loopback(AddressFamily.IPv4, 8080).ToString());
            Assert.Equal("[::1]:8080", SocketAddress.Loopback(AddressFamily.IPv6, 8080).ToString());
            Assert.Equal("(unspecified)", default(SocketAddress).ToString());
        }

        [Fact]
        public void EqualityComparesAllFields()
        {
            var a = SocketAddress.Parse("10.0.0.1", 1);

            Assert.Equal(a, SocketAddress.Parse("10.0.0.1", 1));
            Assert.Equal(a.GetHashCode(), SocketAddress.Parse("10.0.0.1", 1).GetHashCode());
            Assert.NotEqual(a, SocketAddress.Parse("10.0.0.2", 1));
            Assert.NotEqual(a, SocketAddress.Parse("10.0.0.1", 2));
            Assert.NotEqual(SocketAddress.Any(AddressFamily.IPv4, 0), SocketAddress.Any(AddressFamily.IPv6, 0));
        }

        [Fact]
        public void ConstructorRejectsBadInput()
        {
            Assert.Throws<ArgumentException>(() => new SocketAddress(new byte[5], 0));
            Assert.Throws<ArgumentException>(() => new SocketAddress(new byte[4], 0, scopeId: 1));
        }
    }
}

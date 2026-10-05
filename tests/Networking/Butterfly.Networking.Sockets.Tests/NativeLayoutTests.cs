using Butterfly.Networking.Sockets.Native;
using Butterfly.Networking.Sockets.Native.Unix;
using Butterfly.Networking.Sockets.Native.Windows;

namespace Butterfly.Networking.Sockets.Tests
{
    // These run on any OS: they check the byte layouts and error tables for every platform.
    public class NativeLayoutTests
    {
        private static readonly SocketAddress IPv4 = SocketAddress.Parse("192.168.1.2", 0x1F90);
        private static readonly SocketAddress IPv6 = SocketAddress.Parse("fe80::1%7", 0x1F90);

        [Fact]
        public void WritesLinuxAndWindowsSockAddrIn()
        {
            byte[] expected = [2, 0, 0x1F, 0x90, 192, 168, 1, 2, 0, 0, 0, 0, 0, 0, 0, 0];

            Assert.Equal(expected, Write(SockAddrLayout.Linux, IPv4));
            Assert.Equal(expected, Write(SockAddrLayout.Windows, IPv4));
        }

        [Theory]
        [MemberData(nameof(BsdLayouts))]
        public void WritesBsdSockAddrIn(string name, ushort afInet6)
        {
            var layout = name == "Apple" ? SockAddrLayout.Apple : SockAddrLayout.FreeBSD;

            Assert.Equal([16, 2, 0x1F, 0x90, 192, 168, 1, 2, 0, 0, 0, 0, 0, 0, 0, 0], Write(layout, IPv4));

            byte[] ipv6 = Write(layout, IPv6);
            Assert.Equal(28, ipv6.Length);
            Assert.Equal(28, ipv6[0]);
            Assert.Equal(afInet6, ipv6[1]);
        }

        public static TheoryData<string, ushort> BsdLayouts => new() { { "Apple", 30 }, { "FreeBSD", 28 } };

        [Fact]
        public void WritesSockAddrIn6()
        {
            byte[] bytes = Write(SockAddrLayout.Linux, IPv6);

            Assert.Equal(28, bytes.Length);
            Assert.Equal([10, 0, 0x1F, 0x90, 0, 0, 0, 0], bytes[..8]);
            Assert.Equal(IPv6.GetAddressBytes(), bytes[8..24]);
            Assert.Equal(7u, BitConverter.ToUInt32(bytes, 24));
        }

        [Fact]
        public void RoundTripsOnEveryLayout()
        {
            foreach (var layout in new[] { SockAddrLayout.Windows, SockAddrLayout.Linux, SockAddrLayout.Apple, SockAddrLayout.FreeBSD })
            {
                Assert.Equal(IPv4, layout.Read(Write(layout, IPv4)));
                Assert.Equal(IPv6, layout.Read(Write(layout, IPv6)));
            }
        }

        [Fact]
        public void RejectsForeignFamily()
        {
            // A Linux sockaddr_in6 is not a valid Apple one.
            Assert.Throws<NotSupportedException>(() => SockAddrLayout.Apple.Read(Write(SockAddrLayout.Linux, IPv6)));
        }

        [Theory]
        [InlineData(111, 61, 10061, SocketError.ConnectionRefused)]
        [InlineData(98, 48, 10048, SocketError.AddressInUse)]
        [InlineData(104, 54, 10054, SocketError.ConnectionReset)]
        [InlineData(110, 60, 10060, SocketError.TimedOut)]
        [InlineData(11, 35, 10035, SocketError.WouldBlock)]
        [InlineData(115, 36, 10036, SocketError.InProgress)]
        [InlineData(13, 13, 10013, SocketError.AccessDenied)]
        public void MapsErrorsConsistently(int linux, int bsd, int windows, SocketError expected)
        {
            Assert.Equal(expected, UnixErrors.FromLinux(linux));
            Assert.Equal(expected, UnixErrors.FromBsd(bsd));
            Assert.Equal(expected, WindowsSocketErrors.ToSocketError(windows));
        }

        [Fact]
        public void BsdErrnosMatchWinsockMinus10000()
        {
            for (int errno = 35; errno <= 65; errno++)
            {
                var bsd = UnixErrors.FromBsd(errno);
                if (bsd != SocketError.Unknown)
                    Assert.Equal(WindowsSocketErrors.ToSocketError(errno + 10000), bsd);
            }
        }

        private static byte[] Write(SockAddrLayout layout, SocketAddress address)
        {
            Span<byte> buffer = stackalloc byte[SockAddrLayout.MaxLength];
            return buffer[..layout.Write(address, buffer)].ToArray();
        }
    }
}

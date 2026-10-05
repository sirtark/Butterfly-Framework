using Butterfly.Networking.Sockets;

namespace Butterfly.Communication.Tests
{
    public class SupportTests
    {
        [Fact]
        public void SaslPlain() => Assert.Equal("AHVzZXIAcGFzcw==", Sasl.Plain("user", "pass"));

        [Fact]
        public void SaslCramMd5MatchesRfc2195()
        {
            string challenge = Sasl.ToBase64("<1896.697170952@postoffice.reston.mci.net>");
            Assert.Equal("dGltIGI5MTNhNjAyYzdlZGE3YTQ5NWI0ZTZlNzMzNGQzODkw", Sasl.CramMd5(challenge, "tim", "tanstaaftanstaaf"));
        }

        [Fact]
        public void SaslXOAuth2()
            => Assert.Equal("user=a@b.c\u0001auth=Bearer tok\u0001\u0001", Sasl.FromBase64(Sasl.XOAuth2("a@b.c", "tok")));

        [Fact]
        public void ParsesHostsFile()
        {
            var entries = HostsFile.Parse(
            [
                "# comentario",
                "127.0.0.1   localhost loopback",
                "10.0.0.5\tservidor.lan servidor  # alias",
                "::1 localhost",
                "no-es-una-ip nombre",
            ]);

            Assert.Equal(SocketAddress.Parse("10.0.0.5", 0), Assert.Single(entries["SERVIDOR"]));
            Assert.Equal(2, entries["localhost"].Count);
            Assert.False(entries.ContainsKey("nombre"));
        }

        [Theory]
        [InlineData("localhost")]
        [InlineData("api.localhost")]
        [InlineData("127.0.0.1")]
        [InlineData("::1")]
        public void ResolvesWithoutNetwork(string host)
        {
            IReadOnlyList<SocketAddress> addresses = HostResolver.Default.Resolve(host);
            Assert.NotEmpty(addresses);
        }

        [Fact]
        public void CharsetsIncludeLegacyCodePages()
        {
            Assert.Equal(1252, Charsets.GetEncoding("windows-1252").CodePage);
            Assert.Equal(28591, Charsets.GetEncoding("us-ascii").CodePage);
            Assert.Equal(65001, Charsets.GetEncoding("x-desconocido").CodePage);
        }
    }
}

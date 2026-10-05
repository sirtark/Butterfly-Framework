using System.Buffers.Binary;
using System.Text;

using Butterfly.Communication.Testing;
using Butterfly.Networking.Sockets;

namespace Butterfly.Communication.Dns.Tests
{
    public class DnsClientTests
    {
        private static DnsClient ClientFor(SocketAddress server) => new(new DnsClientOptions
        {
            Servers = [server],
            Timeout = TimeSpan.FromMilliseconds(500),
            Attempts = 1,
        });

        [Fact]
        public void ResolvesAddressesThroughCompressedCname()
        {
            using var server = new UdpTestServer(query =>
            {
                var (id, name, type) = Parse(query);
                var response = new ResponseBuilder(id, name, type);
                if (type == DnsRecordType.A)
                {
                    // www.example.com CNAME example.com (pointer to offset 16 = "example.com" inside the question)
                    response.Answer(DnsRecordType.CNAME, [0xC0, 16]);
                    response.AnswerAt([0xC0, 16], DnsRecordType.A, [93, 184, 216, 34]);
                }
                else
                {
                    response.AnswerAt([0xC0, 16], DnsRecordType.AAAA, [0x26, 0x06, 0x28, 0, 0x02, 0x20, 0, 1, 0x2, 0x48, 0x18, 0x93, 0x25, 0xc8, 0x19, 0x46]);
                }
                return response.Build();
            });

            IReadOnlyList<SocketAddress> addresses = ClientFor(server.Address).ResolveAddresses("www.example.com");

            Assert.Equal(["93.184.216.34", "2606:2800:220:1:248:1893:25c8:1946"], addresses.Select(a => a.AddressToString()));
        }

        [Fact]
        public void DecodesCommonRecordTypes()
        {
            using var server = new UdpTestServer(query =>
            {
                var (id, name, type) = Parse(query);
                var response = new ResponseBuilder(id, name, type);
                response.Answer(DnsRecordType.MX, [0, 10, .. Name("mail.example.com")]);
                response.Answer(DnsRecordType.TXT, [.. Text("v=spf1 "), .. Text("-all")]);
                response.Answer(DnsRecordType.SRV, [0, 1, 0, 5, 0x14, 0x66, .. Name("sip.example.com")]);
                response.Answer(DnsRecordType.CAA, [0, 5, .. "issue"u8, .. "letsencrypt.org"u8]);
                response.Answer(DnsRecordType.SOA, [.. Name("ns1.example.com"), .. Name("admin.example.com"),
                    0, 0, 0, 7, 0, 0, 0x0E, 0x10, 0, 0, 0x07, 0x08, 0, 0x12, 0x75, 0, 0, 0, 0x0E, 0x10]);
                return response.Build();
            });

            DnsResponse response = ClientFor(server.Address).Query("example.com", DnsRecordType.ANY);

            MxRecord mx = Assert.Single(response.AnswersOf<MxRecord>());
            Assert.Equal((10, "mail.example.com"), (mx.Preference, mx.Exchange));

            TxtRecord txt = Assert.Single(response.AnswersOf<TxtRecord>());
            Assert.Equal("v=spf1 -all", txt.Text);
            Assert.Equal(2, txt.Strings.Count);

            SrvRecord srv = Assert.Single(response.AnswersOf<SrvRecord>());
            Assert.Equal((1, 5, 5222, "sip.example.com"), (srv.Priority, srv.Weight, srv.Port, srv.Target));

            CaaRecord caa = Assert.Single(response.AnswersOf<CaaRecord>());
            Assert.Equal(("issue", "letsencrypt.org"), (caa.Tag, caa.Value));

            SoaRecord soa = Assert.Single(response.AnswersOf<SoaRecord>());
            Assert.Equal(("ns1.example.com", 7u, TimeSpan.FromHours(1)), (soa.PrimaryServer, soa.Serial, soa.Refresh));
        }

        [Fact]
        public void IgnoresResponsesWithAnotherId()
        {
            using var server = new UdpTestServer(query =>
            {
                var (id, name, type) = Parse(query);
                return new ResponseBuilder((ushort)(id + 1), name, type).Answer(DnsRecordType.A, [6, 6, 6, 6]).Build();
            });

            Assert.Throws<DnsException>(() => ClientFor(server.Address).Query("example.com", DnsRecordType.A));
        }

        [Fact]
        public void RetriesOverTcpWhenTruncated()
        {
            using var tcp = new TestServer(session =>
            {
                int length = BinaryPrimitives.ReadUInt16BigEndian(session.ReadBytes(2));
                var (id, name, type) = Parse(session.ReadBytes(length));
                byte[] response = new ResponseBuilder(id, name, type).Answer(DnsRecordType.A, [10, 0, 0, 1]).Build();
                byte[] prefix = new byte[2];
                BinaryPrimitives.WriteUInt16BigEndian(prefix, (ushort)response.Length);
                session.Write([.. prefix, .. response]);
            });

            // The UDP server lives on the same port as the TCP one, as a real DNS server does.
            using var udp = new UdpTestServer(query =>
            {
                var (id, name, type) = Parse(query);
                return new ResponseBuilder(id, name, type, truncated: true).Build();
            }, tcp.Port);

            DnsResponse response = ClientFor(udp.Address).Query("big.example.com", DnsRecordType.A);

            Assert.False(response.IsTruncated);
            Assert.Equal("10.0.0.1", Assert.Single(response.AnswersOf<AddressRecord>()).Address.AddressToString());
            tcp.Wait();
        }

        [Fact]
        public void ReportsNonExistentNames()
        {
            using var server = new UdpTestServer(query =>
            {
                var (id, name, type) = Parse(query);
                return new ResponseBuilder(id, name, type, code: DnsResponseCode.NameError).Build();
            });

            var exception = Assert.Throws<DnsException>(() => ClientFor(server.Address).ResolveAddresses("nope.example.com"));
            Assert.Equal(DnsResponseCode.NameError, exception.ResponseCode);
        }

        [Fact]
        public void RejectsCompressionLoops()
        {
            using var server = new UdpTestServer(query =>
            {
                var (id, name, type) = Parse(query);
                var response = new ResponseBuilder(id, name, type);
                response.AnswerAt([0xC0, 0x0C], DnsRecordType.CNAME, [0xC0, 0x0C]);
                byte[] message = response.Build();
                // Make the question name point to itself.
                message[12] = 0xC0;
                message[13] = 0x0C;
                return message;
            });

            Assert.Throws<DnsException>(() => ClientFor(server.Address).Query("example.com", DnsRecordType.CNAME));
        }

        [Fact]
        public void BuildsReverseNames()
        {
            Assert.Equal("4.3.2.1.in-addr.arpa", DnsClient.ReverseName(SocketAddress.Parse("1.2.3.4", 0)));
            Assert.StartsWith("1.0.0.0.0.0.0.0", DnsClient.ReverseName(SocketAddress.Parse("::1", 0)));
            Assert.EndsWith("ip6.arpa", DnsClient.ReverseName(SocketAddress.Parse("::1", 0)));
        }

        [Fact]
        public void EncodesInternationalNamesAsPunycode()
        {
            string? asked = null;
            using var server = new UdpTestServer(query =>
            {
                var (id, name, type) = Parse(query);
                asked = name;
                return new ResponseBuilder(id, name, type).Build();
            });

            ClientFor(server.Address).Query("españa.es", DnsRecordType.A);
            Assert.Equal("xn--espaa-rta.es", asked);
        }

        // --- helpers -----------------------------------------------------------------------------------------

        private static (ushort Id, string Name, DnsRecordType Type) Parse(byte[] query)
        {
            ushort id = BinaryPrimitives.ReadUInt16BigEndian(query);
            var labels = new List<string>();
            int offset = 12;
            while (query[offset] != 0)
            {
                labels.Add(Encoding.ASCII.GetString(query, offset + 1, query[offset]));
                offset += query[offset] + 1;
            }
            var type = (DnsRecordType)BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(offset + 1));
            return (id, string.Join('.', labels), type);
        }

        private static byte[] Name(string name)
        {
            var bytes = new List<byte>();
            foreach (string label in name.Split('.'))
            {
                bytes.Add((byte)label.Length);
                bytes.AddRange(Encoding.ASCII.GetBytes(label));
            }
            bytes.Add(0);
            return [.. bytes];
        }

        private static byte[] Text(string value) => [(byte)value.Length, .. Encoding.ASCII.GetBytes(value)];

        private sealed class ResponseBuilder
        {
            private readonly List<byte> _question = [];
            private readonly List<byte> _answers = [];
            private readonly ushort _id;
            private readonly bool _truncated;
            private readonly DnsResponseCode _code;
            private int _answerCount;

            public ResponseBuilder(ushort id, string name, DnsRecordType type, bool truncated = false, DnsResponseCode code = DnsResponseCode.NoError)
            {
                _id = id;
                _truncated = truncated;
                _code = code;
                _question.AddRange(Name(name));
                _question.AddRange([(byte)((ushort)type >> 8), (byte)type, 0, 1]);
            }

            /// <summary>An answer owned by the question name (pointer to offset 12).</summary>
            public ResponseBuilder Answer(DnsRecordType type, byte[] data) => AnswerAt([0xC0, 12], type, data);

            public ResponseBuilder AnswerAt(byte[] owner, DnsRecordType type, byte[] data)
            {
                _answers.AddRange(owner);
                _answers.AddRange([(byte)((ushort)type >> 8), (byte)type, 0, 1, 0, 0, 0x0E, 0x10, (byte)(data.Length >> 8), (byte)data.Length]);
                _answers.AddRange(data);
                _answerCount++;
                return this;
            }

            public byte[] Build()
            {
                ushort flags = (ushort)(0x8180 | (_truncated ? 0x0200 : 0) | (int)_code);
                return [(byte)(_id >> 8), (byte)_id, (byte)(flags >> 8), (byte)flags, 0, 1, 0, (byte)_answerCount, 0, 0, 0, 0,
                    .. _question, .. _answers];
            }
        }
    }
}

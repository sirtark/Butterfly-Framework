using Butterfly.Chrysalis.Http.Http2;
using System.Reflection;
using System.Text;

namespace Butterfly.Chrysalis.Http.Tests
{
    public class HpackTests
    {
        private static (string, string)[] Decode(HpackDecoder decoder, string hex) => [.. decoder.Decode(Convert.FromHexString(hex.Replace(" ", "")))];

        [Fact]
        public void DecodesTheRfcRequestExamplesWithoutHuffman()
        {
            // RFC 7541 C.3: three requests on one connection, sharing the dynamic table.
            var decoder = new HpackDecoder(4096);

            Assert.Equal([(":method", "GET"), (":scheme", "http"), (":path", "/"), (":authority", "www.example.com")],
                Decode(decoder, "8286 8441 0f77 7777 2e65 7861 6d70 6c65 2e63 6f6d"));
            Assert.Equal([(":method", "GET"), (":scheme", "http"), (":path", "/"), (":authority", "www.example.com"), ("cache-control", "no-cache")],
                Decode(decoder, "8286 84be 5808 6e6f 2d63 6163 6865"));
            Assert.Equal([(":method", "GET"), (":scheme", "https"), (":path", "/index.html"), (":authority", "www.example.com"), ("custom-key", "custom-value")],
                Decode(decoder, "8287 85bf 400a 6375 7374 6f6d 2d6b 6579 0c63 7573 746f 6d2d 7661 6c75 65"));
        }

        [Fact]
        public void DecodesTheRfcRequestExamplesWithHuffman()
        {
            // RFC 7541 C.4: the same requests, Huffman-encoded.
            var decoder = new HpackDecoder(4096);

            Assert.Equal([(":method", "GET"), (":scheme", "http"), (":path", "/"), (":authority", "www.example.com")],
                Decode(decoder, "8286 8441 8cf1 e3c2 e5f2 3a6b a0ab 90f4 ff"));
            Assert.Equal([(":method", "GET"), (":scheme", "http"), (":path", "/"), (":authority", "www.example.com"), ("cache-control", "no-cache")],
                Decode(decoder, "8286 84be 5886 a8eb 1064 9cbf"));
            Assert.Equal([(":method", "GET"), (":scheme", "https"), (":path", "/index.html"), (":authority", "www.example.com"), ("custom-key", "custom-value")],
                Decode(decoder, "8287 85bf 4088 25a8 49e9 5ba9 7d7f 8925 a849 e95b b8e8 b4bf"));
        }

        [Fact]
        public void TheHuffmanTableIsACompletePrefixCode()
        {
            // Kraft's equality: a complete prefix code fills the code space exactly.
            var sum = Huffman.Table.Sum(entry => Math.Pow(2, -entry.Length));

            Assert.Equal(257, Huffman.Table.Length);
            Assert.Equal(1.0, sum, 12);
        }

        [Fact]
        public void TheHuffmanTableMatchesTheOneInDotNet()
        {
            // System.Net.Http carries its own copy of RFC 7541 Appendix B: an independent check of every code.
            // The table is exposed as ReadOnlySpan properties: read through typed delegates (Func allows ref structs).
            var huffman = typeof(HttpClient).Assembly.GetType("System.Net.Http.HPack.Huffman", throwOnError: true)!;
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
            var codes = huffman.GetProperty("EncodingTableCodes", flags)!.GetMethod!.CreateDelegate<Func<ReadOnlySpan<uint>>>()();
            var lengths = huffman.GetProperty("EncodingTableBitLengths", flags)!.GetMethod!.CreateDelegate<Func<ReadOnlySpan<byte>>>()();

            Assert.Equal(257, codes.Length);
            for (var symbol = 0; symbol < 257; symbol++)
            {
                // .NET stores codes left-aligned in 32 bits.
                var (code, length) = Huffman.Table[symbol];
                Assert.Equal(lengths[symbol], length);
                Assert.Equal(codes[symbol] >> (32 - length), code);
            }
        }

        [Theory]
        [InlineData("f1e3c2e5f23a6ba0ab90f4ff", "www.example.com")]
        [InlineData("a8eb10649cbf", "no-cache")]
        [InlineData("25a849e95ba97d7f", "custom-key")]
        [InlineData("", "")]
        public void DecodesHuffmanText(string hex, string text) => Assert.Equal(text, Encoding.ASCII.GetString(Huffman.Decode(Convert.FromHexString(hex))));

        [Theory]
        [InlineData("f1e3c2e5f23a6ba0ab90f400")]   // padding with zeros
        [InlineData("f1e3c2e5f23a6ba0ab90f4ffff")] // more than 7 bits of padding
        [InlineData("fffffffc")]                   // EOS
        public void RejectsBadHuffmanText(string hex) => Assert.Throws<HpackException>(() => Huffman.Decode(Convert.FromHexString(hex)));

        [Fact]
        public void TheEncoderRoundTripsThroughTheDecoder()
        {
            var output = new List<byte>();
            (string, string)[] headers = [(":status", "200"), (":status", "418"), ("content-type", "application/grpc"), ("grpc-status", "0"), ("x-long", new string('v', 300))];
            foreach (var (name, value) in headers)
                HpackEncoder.Encode(output, name, value);

            Assert.Equal(0x88, output[0]); // ":status: 200" is static entry 8
            Assert.Equal(headers, new HpackDecoder(4096).Decode([.. output]));
        }

        [Fact]
        public void EvictsOldEntriesWhenTheTableIsFull()
        {
            // Table of 64 bytes: each "custom-key: custom-value" entry takes 54, so the second insert evicts the first.
            var decoder = new HpackDecoder(64);
            Decode(decoder, "400a 6375 7374 6f6d 2d6b 6579 0c63 7573 746f 6d2d 7661 6c75 65");
            Decode(decoder, "4001 61 0161");

            Assert.Equal([("a", "a")], Decode(decoder, "be"));
            Assert.Throws<HpackException>(() => Decode(decoder, "bf"));
        }

        [Theory]
        [InlineData("80")]             // index 0
        [InlineData("ff00")]           // index beyond the tables
        [InlineData("4005 6162")]      // truncated literal
        [InlineData("3fe21f")]         // table size update to 4097, above the limit
        [InlineData("82 20")]          // size update after a header
        public void RejectsMalformedBlocks(string hex) => Assert.Throws<HpackException>(() => Decode(new HpackDecoder(4096), hex));
    }
}

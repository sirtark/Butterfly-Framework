using Butterfly.Serialization.Tests;

namespace Butterfly.Serialization.Cbor.Tests
{
    public class CborFormatTests
    {
        private static readonly CborFormat Cbor = CborFormat.Instance;

        private static SerializationValue Read(string hex) => Cbor.ReadValue(Convert.FromHexString(hex), SerializationProfile.Default);
        private static string Write(SerializationValue value) => Convert.ToHexString(Cbor.Serialize(DynamicType.Value, value)).ToLowerInvariant();

        [Fact]
        public void RoundTripsEveryKind()
        {
            var sample = Samples.Full();

            var bytes = ButterflySerializer.Serialize(sample, Cbor);

            Samples.AssertEqual(sample, ButterflySerializer.Deserialize<AllKinds>(bytes, Cbor), exactTimestampOffset: true);
        }

        // RFC 8949, Appendix A: encodings every implementation must agree on.
        [Theory]
        [InlineData(0L, "00")]
        [InlineData(23L, "17")]
        [InlineData(24L, "1818")]
        [InlineData(100L, "1864")]
        [InlineData(1000L, "1903e8")]
        [InlineData(1000000L, "1a000f4240")]
        [InlineData(1000000000000L, "1b000000e8d4a51000")]
        [InlineData(-1L, "20")]
        [InlineData(-10L, "29")]
        [InlineData(-100L, "3863")]
        [InlineData(-1000L, "3903e7")]
        public void EncodesIntegersAsTheRfc(long value, string hex)
        {
            Assert.Equal(hex, Write(SerializationValue.From(value)));
            Assert.Equal(value, Read(hex).AsInt64());
        }

        [Theory]
        [InlineData("fb3ff199999999999a", 1.1)]
        [InlineData("f93c00", 1.0)]
        [InlineData("f93e00", 1.5)]
        [InlineData("f97bff", 65504.0)]
        [InlineData("fa47c35000", 100000.0)]
        [InlineData("f90001", 5.960464477539063e-8)]
        [InlineData("fbc010666666666666", -4.1)]
        public void ReadsTheRfcFloats(string hex, double expected) => Assert.Equal(expected, Read(hex).AsDouble());

        [Fact]
        public void ReadsTheRfcSpecialFloats()
        {
            Assert.Equal(double.PositiveInfinity, Read("f97c00").AsDouble());
            Assert.True(double.IsNaN(Read("f97e00").AsDouble()));
            Assert.Equal(double.NegativeInfinity, Read("f9fc00").AsDouble());
        }

        [Theory]
        [InlineData("f4", "false")]
        [InlineData("f5", "true")]
        [InlineData("f6", "null")]
        [InlineData("f7", "null")]                                                   // undefined
        [InlineData("6161", "\"a\"")]
        [InlineData("6449455446", "\"IETF\"")]
        [InlineData("62225c", "\"\\\"\\\\\"")]
        [InlineData("80", "[]")]
        [InlineData("83010203", "[1,2,3]")]
        [InlineData("8301820203820405", "[1,[2,3],[4,5]]")]
        [InlineData("a0", "{}")]
        [InlineData("a26161016162820203", "{\"a\":1,\"b\":[2,3]}")]
        [InlineData("5f42010243030405ff", "\"AQIDBAU=\"")]                            // indefinite bytes h'0102030405'
        [InlineData("7f657374726561646d696e67ff", "\"streaming\"")]                  // indefinite text
        [InlineData("9fff", "[]")]
        [InlineData("9f018202039f0405ffff", "[1,[2,3],[4,5]]")]
        [InlineData("bf6346756ef563416d7421ff", "{\"Fun\":true,\"Amt\":-2}")]
        public void ReadsTheRfcExamples(string hex, string expected) => Assert.Equal(expected, Read(hex).ToString());

        [Fact]
        public void ReadsTheRfcTagsForDatesAndNumbers()
        {
            Assert.Equal(DateTimeOffset.Parse("2013-03-21T20:04:00Z"), Read("c074323031332d30332d32315432303a30343a30305a").AsTimestamp());
            Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1363896240), Read("c11a514b67b0").AsTimestamp());
            Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1363896240500), Read("c1fb41d452d9ec200000").AsTimestamp());
            Assert.Equal(18446744073709551616m, Read("c249010000000000000000").AsDecimal());
            Assert.Equal(-18446744073709551617m, Read("c349010000000000000000").AsDecimal());
            Assert.Equal(273.15m, Read("c48221196ab3").AsDecimal());   // 4([-2, 27315])
        }

        [Fact]
        public void WritesDecimalsAndDatesWithStandardTags()
        {
            Assert.Equal("c48221196ab3", Write(SerializationValue.From(273.15m)));
            Assert.StartsWith("c0", Write(SerializationValue.From(DateTimeOffset.UnixEpoch)));
            // A mantissa beyond 64 bits uses a bignum.
            Assert.Equal(79228162514264337593543950335m, Read(Write(SerializationValue.From(decimal.MaxValue))).AsDecimal());
            Assert.Equal(decimal.MinValue, Read(Write(SerializationValue.From(decimal.MinValue))).AsDecimal());
        }

        [Theory]
        [InlineData("18")]                     // truncated argument
        [InlineData("9b7fffffffffffffff")]     // array claiming too many items
        [InlineData("5f6161ff")]               // text chunk inside indefinite bytes
        [InlineData("c4823b7fffffffffffffff01")] // decimal fraction with an absurd exponent
        [InlineData("1c")]                     // reserved additional information
        public void RejectsMalformedInput(string hex) => Assert.Throws<SerializationException>(() => Read(hex));
    }
}

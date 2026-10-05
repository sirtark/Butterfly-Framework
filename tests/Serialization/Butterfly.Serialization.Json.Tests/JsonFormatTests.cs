using Butterfly.Serialization.Tests;
using System.Text.Json;

namespace Butterfly.Serialization.Json.Tests
{
    public class JsonFormatTests
    {
        private static readonly JsonFormat Json = JsonFormat.Instance;

        [Fact]
        public void RoundTripsEveryKind()
        {
            var sample = Samples.Full();

            var json = ButterflySerializer.SerializeToString(sample, Json);

            Samples.AssertEqual(sample, ButterflySerializer.Deserialize<AllKinds>(json, Json), exactTimestampOffset: true);
        }

        [Fact]
        public void ProducesConventionalJson()
        {
            var json = ButterflySerializer.SerializeToString(Samples.Customer(), Json);

            Assert.Equal("""{"id":7,"name":"Ada","creditLimit":1500,"tag_list":["vip","early"],"status":"Active","address":{"street":"Main 1","city":"London"},"scores":{"math":10,"poetry":9},"labels":{"nickname":"Countess"}}""", json);
            // Valid JSON that System.Text.Json reads.
            using var document = JsonDocument.Parse(json);
            Assert.Equal(10, document.RootElement.GetProperty("scores").GetProperty("math").GetInt32());
        }

        [Fact]
        public void TimeSpansAreConstantFormatText()
        {
            var json = ButterflySerializer.SerializeToString(new AllKinds { Elapsed = new TimeSpan(1, 2, 3, 4, 567), Timeout = TimeSpan.FromMilliseconds(-1500) }, Json);
            using var document = JsonDocument.Parse(json);

            Assert.Equal("1.02:03:04.5670000", document.RootElement.GetProperty("elapsed").GetString());
            Assert.Equal("-00:00:01.5000000", document.RootElement.GetProperty("timeout").GetString());
            // ISO 8601 durations (as XML writes them) are accepted too.
            Assert.Equal(TimeSpan.FromMinutes(90), ButterflySerializer.Deserialize<AllKinds>("""{"elapsed":"PT1H30M"}""", Json).Elapsed);
        }

        [Fact]
        public void IsReadableBySystemTextJsonAndBack()
        {
            var json = ButterflySerializer.SerializeToString(Samples.Full(), Json);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            Assert.Equal(long.MaxValue, root.GetProperty("big").GetInt64());
            Assert.Equal(12345.6789m, root.GetProperty("amount").GetDecimal());
            Assert.Equal(new byte[] { 0, 1, 2, 255 }, root.GetProperty("data").GetBytesFromBase64());
            Assert.Equal("archived", root.GetProperty("status").GetString());
            Assert.Equal("dark", root.GetProperty("json").GetProperty("theme").GetString());
            Assert.Equal(3, root.GetProperty("anything").GetProperty("count").GetInt32());
        }

        [Theory]
        [InlineData("""{"number":"x"}""", "number", "expected a 32-bit integer.")]
        [InlineData("""{"number":2147483648}""", "number", "expected a 32-bit integer.")]
        [InlineData("""{"status":"Deleted"}""", "status", "expected one of Draft, Active, archived.")]
        [InlineData("""{"numbers":[1,"two"]}""", "numbers[1]", "expected a 32-bit integer.")]
        [InlineData("""{"address":{"street":5}}""", "address.street", "expected a string.")]
        [InlineData("""{"when":"yesterday"}""", "when", "'yesterday' is not a valid timestamp.")]
        [InlineData("""{"places":[]}""", "places", "expected a map.")]
        public void ErrorsPointAtTheBadValue(string json, string path, string problem)
        {
            var exception = Assert.Throws<SerializationException>(() => ButterflySerializer.Deserialize<AllKinds>(json, Json));

            Assert.Equal(path, exception.Path);
            Assert.Equal(problem, exception.Problem);
        }

        [Fact]
        public void MalformedJsonIsASerializationError()
        {
            Assert.StartsWith("invalid JSON", Assert.Throws<SerializationException>(() => ButterflySerializer.Deserialize<AllKinds>("{oops", Json)).Message);
        }

        [Fact]
        public void IsLenientWhereItIsSafe()
        {
            var value = ButterflySerializer.Deserialize<AllKinds>("""{"BIG":"9007199254740993","amount":"1.5","status":1,"maybe":null,"numbers":null,/*comment*/"unknown":{"x":[1]}}""", Json);

            Assert.Equal(9007199254740993L, value.Big);
            Assert.Equal(1.5m, value.Amount);
            Assert.Equal(Status.Active, value.Status);
            Assert.Null(value.Maybe);
            Assert.Empty(value.Numbers);
        }

        [Fact]
        public void HandlesSpecialDoubles()
        {
            Assert.Equal("\"NaN\"", ButterflySerializer.SerializeToString(double.NaN, Json));
            Assert.Equal(double.NegativeInfinity, ButterflySerializer.Deserialize<double>("\"-Infinity\"", Json));
        }

        [Fact]
        public void ReadsAndWritesSchemalessValues()
        {
            var value = Json.ReadValue("""{"a":1,"b":[true,null,"x",1.25,1e400]}"""u8.ToArray(), SerializationProfile.Default);

            Assert.Equal(ValueKind.Integer, value["a"]!.Kind);
            Assert.Equal(ValueKind.Decimal, value["b"]![3].Kind);
            Assert.Equal(ValueKind.Number, value["b"]![4].Kind);
            Assert.Equal("""{"a":1,"b":[true,null,"x",1.25,"Infinity"]}""", System.Text.Encoding.UTF8.GetString(Json.Serialize(DynamicType.Value, value)));
        }
    }
}

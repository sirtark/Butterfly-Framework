using Butterfly.Serialization.Tests;
using global::MessagePack;
using global::MessagePack.Resolvers;

namespace Butterfly.Serialization.MessagePack.Tests
{
    // MessagePack-CSharp, the reference .NET implementation, reads what we write and writes what we read.
    public class MessagePackFormatTests
    {
        private static readonly MessagePackFormat Format = MessagePackFormat.Instance;
        private static readonly MessagePackSerializerOptions Contractless = MessagePackSerializerOptions.Standard.WithResolver(ContractlessStandardResolver.Instance);

        [Fact]
        public void RoundTripsEveryKind()
        {
            var sample = Samples.Full();

            var bytes = ButterflySerializer.Serialize(sample, Format);

            Samples.AssertEqual(sample, ButterflySerializer.Deserialize<AllKinds>(bytes, Format));
        }

        [Fact]
        public void TheReferenceImplementationReadsOurOutput()
        {
            var bytes = ButterflySerializer.Serialize(Samples.Customer(), Format);

            Assert.Equal("""{"id":7,"name":"Ada","creditLimit":1500,"tag_list":["vip","early"],"status":1,"address":{"street":"Main 1","city":"London"},"scores":{"math":10,"poetry":9},"labels":{"nickname":"Countess"}}""",
                MessagePackSerializer.ConvertToJson(bytes));
        }

        [Fact]
        public void ReadsTheReferenceImplementationsOutput()
        {
            var written = MessagePackSerializer.Serialize(new Dictionary<string, object?>
            {
                ["id"] = 1, ["name"] = "Bob", ["status"] = 10, ["tag_list"] = new[] { "x" },
                ["scores"] = new Dictionary<string, object?> { ["a"] = (byte)200, ["b"] = -70000 },
                ["address"] = new Dictionary<string, object?> { ["street"] = "S", ["city"] = "C" }
            }, Contractless);

            var customer = ButterflySerializer.Deserialize<Customer>(written, Format);

            Assert.Equal("Bob", customer.Name);
            Assert.Equal(Status.Archived, customer.Status);
            Assert.Equal(["x"], customer.Tags);
            Assert.Equal(200, customer.Scores["a"]);
            Assert.Equal(-70000, customer.Scores["b"]);
            Assert.Equal(new Address("S", "C"), customer.Address);
        }

        [Theory]
        [InlineData("1970-01-01T00:00:01Z")]                 // 32-bit form
        [InlineData("2026-10-04T15:30:45.1234567Z")]         // 64-bit form (nanoseconds)
        [InlineData("2600-01-01T00:00:00.5Z")]               // 96-bit form (beyond 34-bit seconds)
        [InlineData("1900-01-01T00:00:00Z")]                 // negative seconds: 96-bit form
        public void TimestampsUseTheStandardExtension(string text)
        {
            var timestamp = DateTimeOffset.Parse(text, System.Globalization.CultureInfo.InvariantCulture);

            var ours = ButterflySerializer.Serialize(timestamp, Format);
            var theirs = MessagePackSerializer.Serialize(timestamp.UtcDateTime, MessagePackSerializerOptions.Standard);

            Assert.Equal(theirs, ours);
            Assert.Equal(timestamp, ButterflySerializer.Deserialize<DateTimeOffset>(theirs, Format));
        }

        [Theory]
        [InlineData(0L)]
        [InlineData(127L)]
        [InlineData(128L)]
        [InlineData(-32L)]
        [InlineData(-33L)]
        [InlineData(65536L)]
        [InlineData(long.MinValue)]
        [InlineData(long.MaxValue)]
        public void IntegersUseTheShortestForm(long value)
        {
            Assert.Equal(MessagePackSerializer.Serialize(value), ButterflySerializer.Serialize(value, Format));
        }

        [Fact]
        public void StringsOfEverySize()
        {
            foreach (var length in new[] { 0, 31, 32, 255, 256, 70000 })
            {
                var text = new string('x', length);
                Assert.Equal(MessagePackSerializer.Serialize(text), ButterflySerializer.Serialize(text, Format));
            }
        }

        [Theory]
        [InlineData("91")]          // array of 1 with nothing after it
        [InlineData("dd7fffffff")]  // array claiming 2 billion items
        [InlineData("c1")]          // reserved code
        [InlineData("c0c0")]        // trailing data
        public void RejectsMalformedInput(string hex)
        {
            Assert.Throws<SerializationException>(() => Format.ReadValue(Convert.FromHexString(hex), SerializationProfile.Default));
        }
    }
}

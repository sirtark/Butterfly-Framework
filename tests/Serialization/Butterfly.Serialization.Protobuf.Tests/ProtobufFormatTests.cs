using Butterfly.Serialization.Tests;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using System.Text.Json.Nodes;

namespace Butterfly.Serialization.Protobuf.Tests
{
    public class ProtobufFormatTests
    {
        private static readonly ProtobufFormat Protobuf = ProtobufFormat.Instance;

        [Fact]
        public void RoundTripsEveryKind()
        {
            var sample = Samples.Full();

            var bytes = ButterflySerializer.Serialize(sample, Protobuf);

            // Timestamps travel in UTC: the instant survives, the offset does not.
            Samples.AssertEqual(sample, ButterflySerializer.Deserialize<AllKinds>(bytes, Protobuf));
        }

        [Fact]
        public void MatchesTheReferenceImplementation()
        {
            var customer = new Customer { Id = 150, Name = "Ada", Tags = ["a"], Status = Status.Archived, Scores = new() { ["x"] = 3 } };

            // The same message written with Google.Protobuf, field by field.
            using var expected = new MemoryStream();
            var output = new CodedOutputStream(expected);
            output.WriteTag(1, WireFormat.WireType.Varint); output.WriteInt32(150);
            output.WriteTag(2, WireFormat.WireType.LengthDelimited); output.WriteString("Ada");
            output.WriteTag(20, WireFormat.WireType.LengthDelimited); output.WriteString("a");   // explicit [Serialize(Number = 20)]
            output.WriteTag(5, WireFormat.WireType.Varint); output.WriteEnum(10);
            // map<string, int32> scores = 7: an entry message { key = 1; value = 2; }
            output.WriteTag(7, WireFormat.WireType.LengthDelimited);
            output.WriteLength(5);
            output.WriteTag(1, WireFormat.WireType.LengthDelimited); output.WriteString("x");
            output.WriteTag(2, WireFormat.WireType.Varint); output.WriteInt32(3);
            output.Flush();

            Assert.Equal(expected.ToArray(), ButterflySerializer.Serialize(customer, Protobuf));
        }

        [Fact]
        public void DynamicValuesAreGoogleProtobufValues()
        {
            var node = JsonNode.Parse("""{"name":"x","sizes":[1,2.5],"on":true,"none":null,"nested":{"a":"b"}}""");

            // A root that is not a message is wrapped: field 1 holds the value.
            var bytes = ButterflySerializer.Serialize(node, Protobuf);
            var input = new CodedInputStream(bytes);
            Assert.Equal(WireFormat.MakeTag(1, WireFormat.WireType.LengthDelimited), input.ReadTag());
            var value = new Value();
            input.ReadMessage(value);

            Assert.Equal("""{ "name": "x", "sizes": [ 1, 2.5 ], "on": true, "none": null, "nested": { "a": "b" } }""", value.ToString());
            Assert.Equal(node!.ToJsonString(), ButterflySerializer.Deserialize<JsonNode>(bytes, Protobuf)!.ToJsonString());
        }

        [Fact]
        public void ReadsWhatGoogleProtobufWrites()
        {
            var value = Value.ForStruct(new Struct { Fields = { ["count"] = Value.ForNumber(3), ["tags"] = Value.ForList(Value.ForString("a")) } });
            using var stream = new MemoryStream();
            var output = new CodedOutputStream(stream);
            output.WriteTag(1, WireFormat.WireType.LengthDelimited);
            output.WriteMessage(value);
            output.Flush();

            var read = ButterflySerializer.Deserialize<SerializationValue>(stream.ToArray(), Protobuf);

            Assert.Equal(3, read["count"]!.AsInt64());
            Assert.Equal("a", read["tags"]![0].AsString());
        }

        [Fact]
        public void TimeSpansAreGoogleProtobufDurations()
        {
            var bytes = ButterflySerializer.Serialize(new AllKinds { Elapsed = new TimeSpan(1, 2, 3, 4, 567), Timeout = TimeSpan.FromMilliseconds(-1500) }, Protobuf);

            // Read back with Google.Protobuf: every field but the durations is skipped.
            var durations = new List<Duration>();
            var input = new CodedInputStream(bytes);
            for (uint tag; (tag = input.ReadTag()) != 0;)
            {
                if (WireFormat.GetTagWireType(tag) == WireFormat.WireType.LengthDelimited && WireFormat.GetTagFieldNumber(tag) >= 21)
                {
                    var duration = new Duration();
                    input.ReadMessage(duration);
                    durations.Add(duration);
                }
                else
                    input.SkipLastField();
            }

            Assert.Equal([Duration.FromTimeSpan(new TimeSpan(1, 2, 3, 4, 567)), Duration.FromTimeSpan(TimeSpan.FromMilliseconds(-1500))], durations);
        }

        [Fact]
        public void ProfilesHideMembersOnTheWire()
        {
            var admin = ButterflySerializer.Serialize(Samples.Customer(), Protobuf, new SerializationProfile("Admin"));
            var publicView = ButterflySerializer.Serialize(Samples.Customer(), Protobuf, new SerializationProfile("Public"));

            Assert.Equal("ada@example.com", ButterflySerializer.Deserialize<Customer>(admin, Protobuf, new SerializationProfile("Admin")).Email);
            Assert.Null(ButterflySerializer.Deserialize<Customer>(admin, Protobuf).Email);   // the reader's profile hides it too
            Assert.Equal(0m, ButterflySerializer.Deserialize<Customer>(publicView, Protobuf, new SerializationProfile("Admin")).CreditLimit);
        }

        [Theory]
        [InlineData("08", "the message is truncated.")]
        [InlineData("32FF", "the message is truncated.")]
        [InlineData("0A0161", "'Flag': wire type 2 does not match the field (expected 0).")]
        [InlineData("00", "invalid field number 0.")]
        public void RejectsMalformedInput(string hex, string message)
        {
            var exception = Assert.Throws<SerializationException>(() => ButterflySerializer.Deserialize<AllKinds>(Convert.FromHexString(hex), Protobuf));

            Assert.EndsWith(message, exception.Message);
        }
    }
}

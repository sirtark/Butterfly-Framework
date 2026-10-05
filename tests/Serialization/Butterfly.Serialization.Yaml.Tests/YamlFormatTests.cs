using Butterfly.Serialization.Tests;
using YamlDotNet.RepresentationModel;

namespace Butterfly.Serialization.Yaml.Tests
{
    public class YamlFormatTests
    {
        private static readonly YamlFormat Yaml = YamlFormat.Instance;

        private static SerializationValue Read(string yaml) => Yaml.ReadValue(System.Text.Encoding.UTF8.GetBytes(yaml), SerializationProfile.Default);

        [Fact]
        public void RoundTripsEveryKind()
        {
            var sample = Samples.Full();

            var yaml = ButterflySerializer.SerializeToString(sample, Yaml);

            Samples.AssertEqual(sample, ButterflySerializer.Deserialize<AllKinds>(yaml, Yaml), exactTimestampOffset: true);
        }

        [Fact]
        public void WritesConventionalBlockYaml()
        {
            var yaml = ButterflySerializer.SerializeToString(Samples.Customer(), Yaml);

            Assert.Equal("""
                id: 7
                name: Ada
                creditLimit: 1500
                tag_list:
                  - vip
                  - early
                status: Active
                address:
                  street: Main 1
                  city: London
                scores:
                  math: 10
                  poetry: 9
                labels:
                  nickname: Countess

                """.ReplaceLineEndings("\n"), yaml);
        }

        [Fact]
        public void YamlDotNetReadsOurOutput()
        {
            var yaml = ButterflySerializer.SerializeToString(Samples.Full(), Yaml);

            var stream = new YamlStream();
            stream.Load(new StringReader(yaml));
            var root = (YamlMappingNode)stream.Documents[0].RootNode;

            Assert.Equal(Samples.Full().Text, ((YamlScalarNode)root["text"]).Value);
            Assert.Equal("-42", ((YamlScalarNode)root["number"]).Value);
            Assert.Equal("Buenos Aires", ((YamlScalarNode)((YamlMappingNode)root["address"])["city"]).Value);
            Assert.Equal(4, ((YamlSequenceNode)root["numbers"]).Children.Count);
            Assert.Equal("tag:yaml.org,2002:binary", ((YamlScalarNode)root["data"]).Tag.Value);
            Assert.Equal("dark", ((YamlScalarNode)((YamlMappingNode)root["json"])["theme"]).Value);
        }

        [Fact]
        public void ReadsWhatYamlDotNetWrites()
        {
            var yaml = new YamlDotNet.Serialization.SerializerBuilder().Build().Serialize(new Dictionary<string, object>
            {
                ["id"] = 3,
                ["name"] = "multi\nline \"quoted\" text: with # signs",
                ["tag_list"] = new[] { "a", "b c" },
                ["address"] = new Dictionary<string, object> { ["street"] = "S", ["city"] = "C" },
                ["scores"] = new Dictionary<string, object> { ["x"] = 1 }
            });

            var customer = ButterflySerializer.Deserialize<Customer>(yaml, Yaml);

            Assert.Equal(3, customer.Id);
            Assert.Equal("multi\nline \"quoted\" text: with # signs", customer.Name);
            Assert.Equal(["a", "b c"], customer.Tags);
            Assert.Equal(new Address("S", "C"), customer.Address);
            Assert.Equal(1, customer.Scores["x"]);
        }

        [Fact]
        public void QuotesStringsThatWouldReadAsSomethingElse()
        {
            foreach (var text in new[] { "123", "true", "null", "~", "", " padded", "a: b", "- dash", "#hash", "0x1F", "1e5", "yes: no", "[x]", "{y}", "line\nbreak", "tab\there", "quote\"s" })
            {
                var yaml = ButterflySerializer.SerializeToString(text, Yaml);
                Assert.Equal(text, ButterflySerializer.Deserialize<string>(yaml, Yaml));
            }
            Assert.Equal("plain text\n", ButterflySerializer.SerializeToString("plain text", Yaml));
        }

        [Fact]
        public void ResolvesPlainScalarsWithTheCoreSchema()
        {
            var value = Read("""
                a: 12
                b: -3.5
                c: 1e3
                d: true
                e: ~
                f: .inf
                g: 0x1F
                h: 0o17
                i: hello world
                j: 2026-10-04
                k:
                """);

            Assert.Equal("""{"a":12,"b":-3.5,"c":1000,"d":true,"e":null,"f":Infinity,"g":31,"h":15,"i":"hello world","j":"2026-10-04","k":null}""", value.ToString());
        }

        [Fact]
        public void ReadsBlockScalars()
        {
            var value = Read("""
                literal: |
                  line 1
                    indented
                  line 3
                folded: >
                  folded
                  text

                  new paragraph
                stripped: |-
                  no newline
                kept: |+
                  keep

                after: x
                """);

            Assert.Equal("line 1\n  indented\nline 3\n", value["literal"]!.AsString());
            Assert.Equal("folded text\nnew paragraph\n", value["folded"]!.AsString());
            Assert.Equal("no newline", value["stripped"]!.AsString());
            Assert.Equal("keep\n\n", value["kept"]!.AsString());
            Assert.Equal("x", value["after"]!.AsString());
        }

        [Fact]
        public void ReadsFlowCollectionsCommentsAnchorsAndQuotes()
        {
            var value = Read("""
                %YAML 1.2
                ---
                # a comment
                defaults: &base {retries: 3, hosts: [a, "b, c"]}   # trailing comment
                copy: *base
                list: [1, two, {x: y},]
                single: 'it''s # not a comment'
                double: "tab\there é \x41"
                multi: [
                  1,
                  2
                ]

                """ + "nested:\n- - 1\n  - 2\n- key: v\n  other: w\n...\nignored: true\n");

            Assert.Equal(3, value["copy"]!["retries"]!.AsInt64());
            Assert.Equal("b, c", value["defaults"]!["hosts"]![1].AsString());
            Assert.Equal("""[1,"two",{"x":"y"}]""", value["list"]!.ToString());
            Assert.Equal("it's # not a comment", value["single"]!.AsString());
            Assert.Equal("tab\there é A", value["double"]!.AsString());
            Assert.Equal("[1,2]", value["multi"]!.ToString());
            Assert.Equal("""[[1,2],{"key":"v","other":"w"}]""", value["nested"]!.ToString());
            Assert.Null(value["ignored"]);
        }

        [Fact]
        public void BytesUseTheBinaryTag()
        {
            Assert.Equal("!!binary \"AQID\"\n", ButterflySerializer.SerializeToString(new byte[] { 1, 2, 3 }, Yaml));
            Assert.Equal(new byte[] { 1, 2, 3 }, ButterflySerializer.Deserialize<byte[]>("!!binary |\n  AQ\n  ID\n", Yaml));
        }

        [Theory]
        [InlineData("a: 1\n  b: 2\n")]
        [InlineData("a: \"unterminated\n")]
        [InlineData("a: [1, 2\n")]
        [InlineData("a: *missing\n")]
        [InlineData("\ta: 1\n")]
        [InlineData("? complex\n: key\n")]
        public void RejectsMalformedYaml(string yaml) => Assert.Throws<SerializationException>(() => Read(yaml));

        [Fact]
        public void PlainScalarsResolveToTheContractTypes()
        {
            // "42" read as an integer goes into a string member, and "true" text into a bool.
            var address = ButterflySerializer.Deserialize<Address>("street: 42\ncity: true\n", Yaml);

            Assert.Equal(new Address("42", "true"), address);
        }
    }
}

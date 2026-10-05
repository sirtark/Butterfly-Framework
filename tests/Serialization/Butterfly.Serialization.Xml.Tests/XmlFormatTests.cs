using Butterfly.Serialization.Tests;
using System.Text;
using System.Xml.Linq;

namespace Butterfly.Serialization.Xml.Tests
{
    public class XmlFormatTests
    {
        private static readonly XmlFormat Xml = XmlFormat.Instance;

        [Fact]
        public void RoundTripsEveryKind()
        {
            var sample = Samples.Full();

            var xml = ButterflySerializer.SerializeToString(sample, Xml);

            Samples.AssertEqual(sample, ButterflySerializer.Deserialize<AllKinds>(xml, Xml), exactTimestampOffset: true);
        }

        [Fact]
        public void WritesOneElementPerMemberAndRepeatsLists()
        {
            var document = XDocument.Parse(ButterflySerializer.SerializeToString(Samples.Customer(), Xml));
            var root = document.Root!;

            Assert.Equal("Customer", root.Name.LocalName);
            Assert.Equal("Ada", root.Element("Name")!.Value);
            Assert.Equal(["vip", "early"], root.Elements("tag_list").Select(tag => tag.Value));
            Assert.Equal("London", root.Element("Address")!.Element("City")!.Value);
            Assert.Equal(["math", "poetry"], root.Element("Scores")!.Elements("entry").Select(entry => (string)entry.Attribute("key")!));
            Assert.Null(root.Element("Email"));      // hidden in the default profile
            Assert.Null(root.Element("Password"));   // never serialized
        }

        [Fact]
        public void DynamicValuesKeepTheirKinds()
        {
            var sample = new AllKinds { Anything = new Dictionary<string, object?> { ["n"] = 5L, ["d"] = 1.5m, ["list"] = new List<object?> { true, null } } };

            var root = XDocument.Parse(ButterflySerializer.SerializeToString(sample, Xml)).Root!;
            var anything = root.Element("Anything")!;

            Assert.Equal("object", (string?)anything.Attribute("type"));
            Assert.Equal("integer", (string?)anything.Element("n")!.Attribute("type"));
            Assert.Equal("decimal", (string?)anything.Element("d")!.Attribute("type"));
            Assert.Equal(["boolean", "null"], anything.Element("list")!.Elements("item").Select(item => (string?)item.Attribute("type")));
        }

        [Fact]
        public void ProfilesChooseNamesAndMembers()
        {
            var profile = new SerializationProfile("Admin") { Naming = NamingPolicy.SnakeCase };

            var root = XDocument.Parse(ButterflySerializer.SerializeToString(Samples.Customer(), Xml, profile)).Root!;

            Assert.Equal("ada@example.com", root.Element("email")!.Value);
            Assert.Equal("1500", root.Element("credit_limit")!.Value);
            Assert.Equal(1500m, ButterflySerializer.Deserialize<Customer>(root.ToString(), Xml, profile).CreditLimit);
        }

        [Fact]
        public void SerializesListsAndScalarsAtTheRoot()
        {
            var xml = ButterflySerializer.SerializeToString(new List<Address> { new("A", "B") }, Xml);

            Assert.Equal("List", XDocument.Parse(xml).Root!.Name.LocalName);
            Assert.Equal(new Address("A", "B"), Assert.Single(ButterflySerializer.Deserialize<List<Address>>(xml, Xml)));
            Assert.Equal(42, ButterflySerializer.Deserialize<int>(ButterflySerializer.SerializeToString(42, Xml), Xml));
        }

        [Fact]
        public void UsesTheConfiguredNamespace()
        {
            var format = new XmlFormat("urn:example");

            var root = XDocument.Parse(format.SerializeToString(SerializationRegistry.Get<Address>(), new Address("A", "B"))).Root!;

            Assert.Equal("urn:example", root.Name.NamespaceName);
            Assert.Equal("A", root.Element(XName.Get("Street", "urn:example"))!.Value);
        }

        [Fact]
        public void RejectsDtds()
        {
            const string xml = """<?xml version="1.0"?><!DOCTYPE a [<!ENTITY x SYSTEM "file:///c:/windows/win.ini">]><Address><Street>&x;</Street></Address>""";

            var exception = Assert.Throws<SerializationException>(() => ButterflySerializer.Deserialize<Address>(xml, Xml));

            Assert.Contains("DTD", exception.Message);
        }

        [Theory]
        [InlineData("<AllKinds><Number>x</Number></AllKinds>", "Number")]
        [InlineData("<AllKinds><Numbers>1</Numbers><Numbers>two</Numbers></AllKinds>", "Numbers[1]")]
        [InlineData("<AllKinds><Address><City/></Address><Places><entry>no key</entry></Places></AllKinds>", "Places")]
        public void ErrorsPointAtTheBadValue(string xml, string path)
        {
            Assert.Equal(path, Assert.Throws<SerializationException>(() => ButterflySerializer.Deserialize<AllKinds>(xml, Xml)).Path);
        }

        [Fact]
        public void IndentsWhenAsked()
        {
            var xml = Encoding.UTF8.GetString(Xml.Serialize(SerializationRegistry.Get<Address>(), new Address("A", "B"), new SerializationProfile("Pretty") { Indented = true }));

            Assert.Contains("\n  <Street>A</Street>", xml.ReplaceLineEndings("\n"));
        }
    }
}

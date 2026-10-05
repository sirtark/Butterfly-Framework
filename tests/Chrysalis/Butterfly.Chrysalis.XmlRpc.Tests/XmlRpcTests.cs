using Butterfly.Chrysalis.Http;
using Butterfly.Chrysalis.Tests;
using System.Net;
using System.Text;
using System.Xml.Linq;

namespace Butterfly.Chrysalis.XmlRpc.Tests
{
    public class XmlRpcTests : IAsyncLifetime
    {
        private HttpServer http = null!;
        private HttpClient client = null!;

        public Task InitializeAsync()
        {
            (http, _) = TestServers.StartChrysalis((http, chrysalis) => http.MapXmlRpc(chrysalis));
            client = TestServers.Client(http);
            return Task.CompletedTask;
        }

        public async Task DisposeAsync()
        {
            client.Dispose();
            await http.StopAsync();
        }

        private async Task<XElement> Call(string method, params string[] values)
        {
            var parameters = string.Concat(values.Select(value => $"<param><value>{value}</value></param>"));
            var request = $"<?xml version=\"1.0\"?><methodCall><methodName>{method}</methodName><params>{parameters}</params></methodCall>";
            var response = await client.PostAsync("/xmlrpc", new StringContent(request, Encoding.UTF8, "text/xml"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return XDocument.Parse(await response.Content.ReadAsStringAsync()).Root!;
        }

        private static XElement Value(XElement response) => response.Element("params")!.Element("param")!.Element("value")!;

        private static (int Code, string Message) Fault(XElement response)
        {
            var members = response.Element("fault")!.Element("value")!.Element("struct")!.Elements("member")
                .ToDictionary(member => member.Element("name")!.Value, member => member.Element("value")!.Elements().Single().Value);
            return (int.Parse(members["faultCode"]), members["faultString"]);
        }

        [Fact]
        public async Task ListsTheMethods()
        {
            var methods = Value(await Call("system.listMethods")).Descendants("string").Select(element => element.Value).ToList();

            Assert.Contains("InventoryService.GetProduct", methods);
            Assert.Equal(10, methods.Count);
        }

        [Fact]
        public async Task CallsWithPositionalParameters()
        {
            var first = await Call("InventoryService.AddStock", "<int>1</int>", "<i4>2</i4>");
            var second = await Call("InventoryService.AddStock", "<int>1</int>", "<int>5</int>");

            Assert.Equal("<int>2</int>", Value(first).Elements().Single().ToString());
            Assert.Equal("7", Value(second).Value);
        }

        [Fact]
        public async Task ReturnsStructsAndArrays()
        {
            var product = Value(await Call("InventoryService.GetProduct", "<int>1</int>")).Element("struct")!;
            var members = product.Elements("member").ToDictionary(member => member.Element("name")!.Value, member => member.Element("value")!.Elements().Single());

            Assert.Equal("<int>1</int>", members["Id"].ToString());
            Assert.Equal("<string>Dune</string>", members["Name"].ToString());
            Assert.Equal("<string>9.99</string>", members["Price"].ToString());
            Assert.Equal("<string>Books</string>", members["Category"].ToString());
            Assert.Equal(["sci-fi", "classic"], members["Tags"].Descendants("string").Select(tag => tag.Value));
            Assert.Equal("<dateTime.iso8601>20260102T03:04:05</dateTime.iso8601>", members["CreatedAt"].ToString());
        }

        [Fact]
        public async Task ReadsStructsAndUntypedStrings()
        {
            const string product = """
                <struct>
                  <member><name>Id</name><value><i4>20</i4></value></member>
                  <member><name>Name</name><value>Untyped name</value></member>
                  <member><name>Price</name><value><double>3.5</double></value></member>
                  <member><name>Category</name><value><string>Games</string></value></member>
                  <member><name>Tags</name><value><array><data><value>a</value><value><string>b</string></value></data></array></value></member>
                  <member><name>CreatedAt</name><value><dateTime.iso8601>20261004T15:30:00</dateTime.iso8601></value></member>
                  <member><name>Unknown</name><value><boolean>1</boolean></value></member>
                </struct>
                """;

            await Call("InventoryService.AddProduct", product);
            var stored = Value(await Call("InventoryService.GetProduct", "<int>20</int>")).ToString();

            Assert.Contains("Untyped name", stored);
            Assert.Contains("<string>3.5</string>", stored);
            Assert.Contains("20261004T15:30:00", stored);
        }

        [Fact]
        public async Task OptionalParametersAcceptNilAndMayBeOmitted()
        {
            var all = Value(await Call("InventoryService.Search", "<nil/>", "<nil/>"));
            var books = Value(await Call("InventoryService.Search", "<nil/>", "<string>books</string>"));
            var omitted = Value(await Call("InventoryService.Search"));

            Assert.Equal(3, all.Descendants("struct").Count());
            Assert.Single(books.Descendants("struct"));
            Assert.Equal(3, omitted.Descendants("struct").Count());
        }

        [Fact]
        public async Task OperationsWithoutResultReturnTrue()
        {
            Assert.Equal("<boolean>1</boolean>", Value(await Call("InventoryService.Delete", "<int>3</int>")).Elements().Single().ToString());
        }

        [Theory]
        [InlineData("InventoryService.GetProduct", "<int>99</int>", ChrysalisStatus.NotFound, "Product 99 does not exist.")]
        [InlineData("InventoryService.Teleport", "<int>1</int>", ChrysalisStatus.Unimplemented, "No method named 'InventoryService.Teleport'.")]
        [InlineData("InventoryService.GetProduct", "<string>one</string>", ChrysalisStatus.InvalidArgument, "'id': 'one' is not a valid int32.")]
        [InlineData("InventoryService.GetProduct", "<struct/>", ChrysalisStatus.InvalidArgument, "'id': expected a int32, not a struct.")]
        [InlineData("InventoryService.Fail", "<string>Internal</string>", ChrysalisStatus.Internal, "An internal error occurred.")]
        public async Task FaultsCarryTheStatus(string method, string value, ChrysalisStatus status, string message)
        {
            var (code, text) = Fault(await Call(method, value));

            Assert.Equal((int)status, code);
            Assert.Equal(message, text);
        }

        [Fact]
        public async Task TooManyParametersFail()
        {
            Assert.Equal((int)ChrysalisStatus.InvalidArgument, Fault(await Call("InventoryService.Delete", "<int>1</int>", "<int>2</int>")).Code);
        }

        [Fact]
        public async Task RejectsDtds()
        {
            var request = "<?xml version=\"1.0\"?><!DOCTYPE m [<!ENTITY e \"x\">]><methodCall><methodName>&e;</methodName></methodCall>";

            var response = await client.PostAsync("/xmlrpc", new StringContent(request, Encoding.UTF8, "text/xml"));

            Assert.Equal((int)ChrysalisStatus.InvalidArgument, Fault(XDocument.Parse(await response.Content.ReadAsStringAsync()).Root!).Code);
        }

        [Fact]
        public async Task OnlyPostIsAccepted() => Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.GetAsync("/xmlrpc")).StatusCode);
    }
}

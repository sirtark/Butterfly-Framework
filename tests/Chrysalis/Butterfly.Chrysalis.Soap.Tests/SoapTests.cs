using Butterfly.Serialization;
using Butterfly.Serialization.Xml;
using Butterfly.Chrysalis.Http;
using Butterfly.Chrysalis.Tests;
using Butterfly.Chrysalis.Tests.Inventory;
using System.Net;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace Butterfly.Chrysalis.Soap.Tests
{
    public class SoapTests : IAsyncLifetime
    {
        private static readonly XNamespace Soap11 = "http://schemas.xmlsoap.org/soap/envelope/";
        private static readonly XNamespace Soap12 = "http://www.w3.org/2003/05/soap-envelope";
        private static readonly XNamespace Tns = "urn:inventory.v1:InventoryService";
        private static readonly XNamespace Xs = "http://www.w3.org/2001/XMLSchema";

        private HttpServer http = null!;
        private HttpClient client = null!;

        public Task InitializeAsync()
        {
            (http, _) = TestServers.StartChrysalis((http, chrysalis) => http.MapSoap(chrysalis, "/soap"));
            client = TestServers.Client(http);
            return Task.CompletedTask;
        }

        public async Task DisposeAsync()
        {
            client.Dispose();
            await http.StopAsync();
        }

        private async Task<(HttpStatusCode Status, XDocument Document)> Call(string body, bool soap12 = false)
        {
            var soap = soap12 ? Soap12 : Soap11;
            var envelope = $"""<s:Envelope xmlns:s="{soap.NamespaceName}" xmlns:t="{Tns.NamespaceName}"><s:Body>{body}</s:Body></s:Envelope>""";
            var response = await client.PostAsync("/soap/InventoryService",
                new StringContent(envelope, Encoding.UTF8, soap12 ? "application/soap+xml" : "text/xml"));
            return (response.StatusCode, XDocument.Parse(await response.Content.ReadAsStringAsync()));
        }

        private static XElement Result(XDocument response, XNamespace soap, string operation) =>
            response.Root!.Element(soap + "Body")!.Element(Tns + (operation + "Response"))!;

        [Fact]
        public async Task CallsAnOperationWithSoap11()
        {
            var (status, document) = await Call("<t:GetProduct><t:id>1</t:id></t:GetProduct>");

            Assert.Equal(HttpStatusCode.OK, status);
            var product = Result(document, Soap11, "GetProduct").Element(Tns + "GetProductResult")!;
            Assert.Equal("Dune", product.Element(Tns + "Name")!.Value);
            Assert.Equal("9.99", product.Element(Tns + "Price")!.Value);
            Assert.Equal(["sci-fi", "classic"], product.Elements(Tns + "Tags").Select(tag => tag.Value));
        }

        [Fact]
        public async Task CallsAnOperationWithSoap12()
        {
            var (status, document) = await Call("<t:AddStock><t:productId>1</t:productId><t:quantity>3</t:quantity></t:AddStock>", soap12: true);

            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal(Soap12, document.Root!.Name.Namespace);
            Assert.Equal("3", Result(document, Soap12, "AddStock").Element(Tns + "AddStockResult")!.Value);
        }

        [Fact]
        public async Task OperationsWithoutResultAnswerAnEmptyResponse()
        {
            var (_, document) = await Call("<t:Delete><t:id>2</t:id></t:Delete>");

            Assert.Empty(Result(document, Soap11, "Delete").Elements());
        }

        [Fact]
        public async Task RoundTripsEveryKindOfValue()
        {
            var service = ChrysalisRegistry.GetService<IInventoryService>();
            var type = service.FindOperation("Echo")!.ReturnType!;
            var sample = Samples.Full();
            var request = new XElement(Tns + "Echo");
            XmlFormat.Instance.WriteMember(request, Tns + "sample", type, sample, SerializationProfile.Default);

            var (_, document) = await Call(request.ToString());

            var echoed = (Sample)XmlFormat.Instance.ReadElement(Result(document, Soap11, "Echo").Element(Tns + "EchoResult")!, type, SerializationProfile.Default, "")!;
            Samples.AssertEqual(sample, echoed);
        }

        [Fact]
        public async Task ClientErrorsAreClientFaultsWithTheStatus()
        {
            var (status, document) = await Call("<t:GetProduct><t:id>99</t:id></t:GetProduct>");

            Assert.Equal(HttpStatusCode.InternalServerError, status);
            var fault = document.Root!.Element(Soap11 + "Body")!.Element(Soap11 + "Fault")!;
            Assert.Equal("soap:Client", fault.Element("faultcode")!.Value);
            Assert.Equal("Product 99 does not exist.", fault.Element("faultstring")!.Value);
            Assert.Equal("NotFound", fault.Element("detail")!.Element(XName.Get("status", "urn:butterfly:chrysalis"))!.Value);
        }

        [Fact]
        public async Task Soap12FaultsUseSenderAndReceiver()
        {
            var (sender, senderDocument) = await Call("<t:GetProduct><t:id>nope</t:id></t:GetProduct>", soap12: true);
            var (receiver, receiverDocument) = await Call("<t:Fail><t:status>Internal</t:status><t:message>secret</t:message></t:Fail>", soap12: true);

            Assert.Equal(HttpStatusCode.BadRequest, sender);
            Assert.Equal("soap:Sender", senderDocument.Descendants(Soap12 + "Value").Single().Value);
            Assert.Contains("'id'", senderDocument.Descendants(Soap12 + "Text").Single().Value);
            Assert.Equal(HttpStatusCode.InternalServerError, receiver);
            Assert.Equal("soap:Receiver", receiverDocument.Descendants(Soap12 + "Value").Single().Value);
            Assert.DoesNotContain("secret", receiverDocument.ToString());
        }

        [Fact]
        public async Task HeadersThatMustBeUnderstoodAreRefused()
        {
            var envelope = $"""<s:Envelope xmlns:s="{Soap11}"><s:Header><x:Security xmlns:x="urn:x" s:mustUnderstand="1"/></s:Header><s:Body><GetProduct xmlns="{Tns}"><id>1</id></GetProduct></s:Body></s:Envelope>""";

            var response = await client.PostAsync("/soap/InventoryService", new StringContent(envelope, Encoding.UTF8, "text/xml"));

            Assert.Contains("soap:MustUnderstand", await response.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task DtdsAreRejectedSoEntitiesCannotBeAbused()
        {
            // An XXE attempt: the server must neither expand nor resolve the entity.
            var envelope = $"""<?xml version="1.0"?><!DOCTYPE x [<!ENTITY xxe SYSTEM "file:///c:/windows/win.ini">]><s:Envelope xmlns:s="{Soap11}"><s:Body><GetProduct xmlns="{Tns}"><id>&xxe;</id></GetProduct></s:Body></s:Envelope>""";

            var response = await client.PostAsync("/soap/InventoryService", new StringContent(envelope, Encoding.UTF8, "text/xml"));
            var text = await response.Content.ReadAsStringAsync();

            Assert.Contains("soap:Client", text);
            Assert.Contains("DTD", text);
            Assert.DoesNotContain("[fonts]", text);
        }

        [Theory]
        [InlineData("application/json", HttpStatusCode.UnsupportedMediaType)]
        [InlineData("text/xml", HttpStatusCode.InternalServerError)]   // not an envelope: Client fault
        public async Task RejectsWhatIsNotSoap(string contentType, HttpStatusCode status)
        {
            var response = await client.PostAsync("/soap/InventoryService", new StringContent("<hello/>", Encoding.UTF8, contentType));

            Assert.Equal(status, response.StatusCode);
        }

        [Fact]
        public async Task TheEnvelopeVersionMustMatchTheContentType()
        {
            var envelope = $"""<s:Envelope xmlns:s="{Soap12}"><s:Body><GetProduct xmlns="{Tns}"><id>1</id></GetProduct></s:Body></s:Envelope>""";

            var response = await client.PostAsync("/soap/InventoryService", new StringContent(envelope, Encoding.UTF8, "text/xml"));

            Assert.Contains("soap:VersionMismatch", await response.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task UnknownServicesAndOperations()
        {
            Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/soap/Nope", new StringContent("<x/>", Encoding.UTF8, "text/xml"))).StatusCode);
            var (_, document) = await Call("<t:Teleport/>");
            Assert.Equal("Unimplemented", document.Descendants(XName.Get("status", "urn:butterfly:chrysalis")).Single().Value);
        }

        [Fact]
        public async Task ServesAWsdlThatDescribesTheService()
        {
            var wsdl = XDocument.Parse(await client.GetStringAsync("/soap/InventoryService?wsdl"));
            XNamespace w = "http://schemas.xmlsoap.org/wsdl/";

            Assert.Equal("InventoryService", wsdl.Root!.Attribute("name")!.Value);
            Assert.Equal(10, wsdl.Root.Element(w + "portType")!.Elements(w + "operation").Count());
            Assert.Equal(2, wsdl.Root.Elements(w + "binding").Count());
            var addresses = wsdl.Descendants().Where(element => element.Name.LocalName == "address").Select(element => element.Attribute("location")!.Value).ToList();
            Assert.All(addresses, address => Assert.Equal($"http://127.0.0.1:{http.Port()}/soap/InventoryService", address));
        }

        [Fact]
        public async Task TheWsdlSchemaIsValidAndMatchesTheMessages()
        {
            // The XSD inside the WSDL must compile, and real responses must validate against it.
            var wsdl = XDocument.Parse(await client.GetStringAsync("/soap/InventoryService?wsdl"));
            var schemas = new XmlSchemaSet();
            schemas.Add(XmlSchema.Read(wsdl.Descendants(Xs + "schema").Single().CreateReader(), (_, e) => throw e.Exception)!);
            schemas.Compile();

            var service = ChrysalisRegistry.GetService<IInventoryService>();
            var echoRequest = new XElement(Tns + "Echo");
            XmlFormat.Instance.WriteMember(echoRequest, Tns + "sample", service.FindOperation("Echo")!.ReturnType!, Samples.Full(), SerializationProfile.Default);

            foreach (var call in new[] { "<t:GetProduct><t:id>1</t:id></t:GetProduct>", "<t:Search/>", echoRequest.ToString(), "<t:Delete><t:id>3</t:id></t:Delete>" })
            {
                var (_, document) = await Call(call);
                var body = new XDocument(document.Root!.Element(Soap11 + "Body")!.Elements().Single());
                body.Validate(schemas, (_, e) => Assert.Fail($"{call}: {e.Message}"));
            }
        }
    }
}

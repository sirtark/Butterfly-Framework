using Butterfly.Chrysalis.Binary;
using Butterfly.Chrysalis.Grpc;
using Butterfly.Chrysalis.Http;
using Butterfly.Chrysalis.JsonRpc;
using Butterfly.Chrysalis.Rest;
using Butterfly.Serialization.Protobuf;
using Butterfly.Chrysalis.Soap;
using Butterfly.Chrysalis.Tests;
using Butterfly.Chrysalis.Tests.Inventory;
using Butterfly.Chrysalis.XmlRpc;
using Butterfly.Networking.Sockets;
using Grpc.Core;
using Grpc.Net.Client;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace Butterfly.Chrysalis.Integration.Tests
{
    public class AllProtocolsTests
    {
        [Fact]
        public async Task OneServiceAnswersEveryProtocolWithSharedState()
        {
            // One implementation, one pipeline, six protocols.
            var protocols = new List<string>();
            var (http, chrysalis) = TestServers.StartChrysalis((http, chrysalis) => http
                .MapRest(chrysalis, "/api")
                .MapSoap(chrysalis, "/soap")
                .MapJsonRpc(chrysalis, "/rpc")
                .MapXmlRpc(chrysalis, "/xmlrpc")
                .MapGrpc(chrysalis));
            chrysalis.Use((context, next) =>
            {
                lock (protocols)
                    protocols.Add(context.Protocol);
                return next(context);
            });

            var binaryOptions = new ChrysalisBinaryServerOptions();
            binaryOptions.Endpoints.Add((SocketAddress.Loopback(AddressFamily.IPv4, 0), null));
            await using var binary = new ChrysalisBinaryServer(chrysalis, binaryOptions);
            binary.Start();

            try
            {
                using var client = TestServers.Client(http);
                using var http2 = TestServers.Client(http, http2: true);

                // REST
                var rest = await client.PostAsync("/api/inventory/AddStock", new StringContent("""{"productId":7,"quantity":1}""", Encoding.UTF8, "application/json"));
                Assert.Equal("1", await rest.Content.ReadAsStringAsync());

                // JSON-RPC
                var jsonRpc = await client.PostAsync("/rpc", new StringContent("""{"jsonrpc":"2.0","method":"InventoryService.AddStock","params":[7,1],"id":1}""", Encoding.UTF8, "application/json"));
                Assert.Equal(2, JsonDocument.Parse(await jsonRpc.Content.ReadAsStringAsync()).RootElement.GetProperty("result").GetInt32());

                // XML-RPC
                var xmlRpc = await client.PostAsync("/xmlrpc", new StringContent(
                    "<methodCall><methodName>InventoryService.AddStock</methodName><params><param><value><int>7</int></value></param><param><value><int>1</int></value></param></params></methodCall>",
                    Encoding.UTF8, "text/xml"));
                Assert.Equal("3", XDocument.Parse(await xmlRpc.Content.ReadAsStringAsync()).Descendants("int").Single().Value);

                // SOAP 1.2 over HTTP/2
                var soap = await http2.PostAsync("/soap/InventoryService", new StringContent(
                    "<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\"><s:Body><AddStock xmlns=\"urn:inventory.v1:InventoryService\"><productId>7</productId><quantity>1</quantity></AddStock></s:Body></s:Envelope>",
                    Encoding.UTF8, "application/soap+xml"));
                Assert.Equal("4", XDocument.Parse(await soap.Content.ReadAsStringAsync()).Descendants().Single(element => element.Name.LocalName == "AddStockResult").Value);

                // gRPC with the official client
                using (var channel = GrpcChannel.ForAddress($"http://127.0.0.1:{http.Port()}", new GrpcChannelOptions { HttpHandler = new SocketsHttpHandler() }))
                {
                    var operation = ChrysalisRegistry.GetService<IInventoryService>().FindOperation("AddStock")!;
                    var method = new Method<byte[], byte[]>(MethodType.Unary, GrpcMessages.ServiceName(operation.Service), "AddStock",
                        Marshallers.Create(bytes => bytes, bytes => bytes), Marshallers.Create(bytes => bytes, bytes => bytes));
                    var response = await channel.CreateCallInvoker().AsyncUnaryCall(method, null, default,
                        ProtobufFormat.Instance.Serialize(operation.ParametersType, new object?[] { 7, 1 })).ResponseAsync;
                    Assert.Equal(5, ((object?[])ProtobufFormat.Instance.Deserialize(operation.ResultType, response)!)[0]);
                }

                // Binary, through the generated typed client
                await using (var connection = await ChrysalisBinaryClient.ConnectAsync("127.0.0.1", binary.Endpoints[0].Port))
                    Assert.Equal(6, connection.CreateClient<IInventoryService>().AddStock(7, 1));

                Assert.Equal(["REST", "JSON-RPC", "XML-RPC", "SOAP", "gRPC", "Binary"], protocols);
            }
            finally
            {
                await http.StopAsync();
            }
        }

        [Fact]
        public async Task TheSameErrorReadsNaturallyInEveryProtocol()
        {
            var (http, chrysalis) = TestServers.StartChrysalis((http, chrysalis) => http
                .MapRest(chrysalis).MapJsonRpc(chrysalis).MapXmlRpc(chrysalis).MapSoap(chrysalis));
            try
            {
                using var client = TestServers.Client(http);

                var rest = await client.GetAsync("/api/inventory/products/404");
                var jsonRpc = await client.PostAsync("/rpc", new StringContent("""{"jsonrpc":"2.0","method":"InventoryService.GetProduct","params":[404],"id":1}""", Encoding.UTF8, "application/json"));
                var xmlRpc = await client.PostAsync("/xmlrpc", new StringContent("<methodCall><methodName>InventoryService.GetProduct</methodName><params><param><value><int>404</int></value></param></params></methodCall>", Encoding.UTF8, "text/xml"));
                var soap = await client.PostAsync("/soap/InventoryService", new StringContent("<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><GetProduct xmlns=\"urn:inventory.v1:InventoryService\"><id>404</id></GetProduct></s:Body></s:Envelope>", Encoding.UTF8, "text/xml"));

                Assert.Equal(404, (int)rest.StatusCode);
                Assert.Equal(-32005, JsonDocument.Parse(await jsonRpc.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetProperty("code").GetInt32());
                Assert.Equal("5", XDocument.Parse(await xmlRpc.Content.ReadAsStringAsync()).Descendants("int").Single().Value);
                Assert.Contains("<faultcode>soap:Client</faultcode>", await soap.Content.ReadAsStringAsync());
                foreach (var response in new[] { rest, jsonRpc, xmlRpc, soap })
                    Assert.Contains("Product 404 does not exist.", await response.Content.ReadAsStringAsync());
            }
            finally
            {
                await http.StopAsync();
            }
        }
    }
}

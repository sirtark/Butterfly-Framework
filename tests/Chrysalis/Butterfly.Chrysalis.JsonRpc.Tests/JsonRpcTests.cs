using Butterfly.Chrysalis.Http;
using Butterfly.Chrysalis.Tests;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Butterfly.Chrysalis.JsonRpc.Tests
{
    public class JsonRpcTests : IAsyncLifetime
    {
        private HttpServer http = null!;
        private HttpClient client = null!;

        public Task InitializeAsync()
        {
            (http, _) = TestServers.StartChrysalis((http, chrysalis) => http.MapJsonRpc(chrysalis, "/rpc", maxBatchSize: 5));
            client = TestServers.Client(http);
            return Task.CompletedTask;
        }

        public async Task DisposeAsync()
        {
            client.Dispose();
            await http.StopAsync();
        }

        private async Task<(HttpStatusCode Status, string Body)> Post(string json)
        {
            var response = await client.PostAsync("/rpc", new StringContent(json, Encoding.UTF8, "application/json"));
            return (response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task CallsWithPositionalParameters()
        {
            var (_, body) = await Post("""{"jsonrpc":"2.0","method":"InventoryService.AddStock","params":[1,5],"id":1}""");

            Assert.Equal("""{"jsonrpc":"2.0","result":5,"id":1}""", body);
        }

        [Fact]
        public async Task CallsWithNamedParametersAndKeepsStringIds()
        {
            var (_, body) = await Post("""{"jsonrpc":"2.0","method":"InventoryService.GetProduct","params":{"id":2},"id":"req-7"}""");

            using var document = JsonDocument.Parse(body);
            Assert.Equal("Kind of Blue", document.RootElement.GetProperty("result").GetProperty("name").GetString());
            Assert.Equal("req-7", document.RootElement.GetProperty("id").GetString());
        }

        [Fact]
        public async Task OperationsWithoutResultReturnNull()
        {
            var (_, body) = await Post("""{"jsonrpc":"2.0","method":"InventoryService.Delete","params":[3],"id":null}""");

            Assert.Equal("""{"jsonrpc":"2.0","result":null,"id":null}""", body);
        }

        [Fact]
        public async Task NotificationsGetNoResponse()
        {
            var (status, body) = await Post("""{"jsonrpc":"2.0","method":"InventoryService.AddStock","params":[9,1]}""");
            var (_, check) = await Post("""{"jsonrpc":"2.0","method":"InventoryService.AddStock","params":[9,1],"id":2}""");

            Assert.Equal(HttpStatusCode.NoContent, status);
            Assert.Empty(body);
            Assert.Contains("\"result\":2", check); // the notification ran
        }

        [Fact]
        public async Task BatchesKeepOrderAndSkipNotifications()
        {
            var (_, body) = await Post("""
                [
                  {"jsonrpc":"2.0","method":"InventoryService.AddStock","params":[4,1],"id":"a"},
                  {"jsonrpc":"2.0","method":"InventoryService.AddStock","params":[5,1]},
                  {"jsonrpc":"2.0","method":"Missing.Method","id":"b"},
                  42,
                  {"jsonrpc":"2.0","method":"InventoryService.GetProduct","params":[99],"id":"c"}
                ]
                """);

            using var document = JsonDocument.Parse(body);
            var responses = document.RootElement.EnumerateArray().ToList();
            Assert.Equal(4, responses.Count);
            Assert.Equal(1, responses[0].GetProperty("result").GetInt32());
            Assert.Equal(-32601, responses[1].GetProperty("error").GetProperty("code").GetInt32());
            Assert.Equal(-32600, responses[2].GetProperty("error").GetProperty("code").GetInt32());
            Assert.Equal(JsonValueKind.Null, responses[2].GetProperty("id").ValueKind);
            Assert.Equal(-32005, responses[3].GetProperty("error").GetProperty("code").GetInt32());
            Assert.Equal("NotFound", responses[3].GetProperty("error").GetProperty("data").GetProperty("status").GetString());
        }

        [Fact]
        public async Task ABatchOfNotificationsAnswers204()
        {
            var (status, _) = await Post("""[{"jsonrpc":"2.0","method":"InventoryService.AddStock","params":[1,1]}]""");

            Assert.Equal(HttpStatusCode.NoContent, status);
        }

        [Theory]
        [InlineData("{not json", -32700)]
        [InlineData("""{"method":"InventoryService.AddStock","id":1}""", -32600)]                                       // no jsonrpc
        [InlineData("""{"jsonrpc":"1.0","method":"InventoryService.AddStock","id":1}""", -32600)]
        [InlineData("""{"jsonrpc":"2.0","method":5,"id":1}""", -32600)]
        [InlineData("""{"jsonrpc":"2.0","method":"InventoryService.AddStock","params":"x","id":1}""", -32600)]
        [InlineData("""{"jsonrpc":"2.0","method":"InventoryService.AddStock","id":{"bad":1}}""", -32600)]
        [InlineData("[]", -32600)]
        [InlineData("[1,2,3,4,5,6]", -32600)]                                                                           // batch limit
        [InlineData("""{"jsonrpc":"2.0","method":"inventoryservice.addstock","id":1}""", -32601)]                       // case-sensitive
        [InlineData("""{"jsonrpc":"2.0","method":"InventoryService.AddStock","params":[1,2,3],"id":1}""", -32602)]
        [InlineData("""{"jsonrpc":"2.0","method":"InventoryService.AddStock","params":["one",2],"id":1}""", -32602)]
        [InlineData("""{"jsonrpc":"2.0","method":"InventoryService.Fail","params":["Internal","secret"],"id":1}""", -32603)]
        [InlineData("""{"jsonrpc":"2.0","method":"InventoryService.Fail","params":["PermissionDenied","no"],"id":1}""", -32007)]
        public async Task ReportsStandardErrors(string request, int code)
        {
            var (status, body) = await Post(request);

            Assert.Equal(HttpStatusCode.OK, status);
            using var document = JsonDocument.Parse(body);
            Assert.Equal(code, document.RootElement.GetProperty("error").GetProperty("code").GetInt32());
            Assert.DoesNotContain("secret", body);
        }

        [Fact]
        public async Task OnlyPostIsAccepted()
        {
            Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.GetAsync("/rpc")).StatusCode);
        }

        [Fact]
        public void MapsEveryStatusToACode()
        {
            Assert.Equal(-32602, JsonRpcErrorCodes.FromStatus(ChrysalisStatus.InvalidArgument));
            Assert.Equal(-32601, JsonRpcErrorCodes.FromStatus(ChrysalisStatus.Unimplemented));
            Assert.Equal(-32603, JsonRpcErrorCodes.FromStatus(ChrysalisStatus.Internal));
            Assert.Equal(-32016, JsonRpcErrorCodes.FromStatus(ChrysalisStatus.Unauthenticated));
        }
    }
}

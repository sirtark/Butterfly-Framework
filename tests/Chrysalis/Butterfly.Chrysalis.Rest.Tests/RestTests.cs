using Butterfly.Chrysalis.Http;
using Butterfly.Chrysalis.Tests;
using Butterfly.Chrysalis.Tests.Inventory;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

namespace Butterfly.Chrysalis.Rest.Tests
{
    public class RestTests : IAsyncLifetime
    {
        private HttpServer http = null!;
        private ChrysalisServer chrysalis = null!;
        private HttpClient client = null!;

        public Task InitializeAsync()
        {
            (http, chrysalis) = TestServers.StartChrysalis((http, chrysalis) => http.MapRest(chrysalis, "/api"));
            client = TestServers.Client(http);
            return Task.CompletedTask;
        }

        public async Task DisposeAsync()
        {
            client.Dispose();
            await http.StopAsync();
        }

        private static async Task<JsonElement> Json(HttpResponseMessage response) =>
            JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        [Fact]
        public async Task GetsByRouteParameter()
        {
            var response = await client.GetAsync("/api/inventory/products/1");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
            Assert.Equal("""{"id":1,"name":"Dune","price":9.99,"category":"Books","tags":["sci-fi","classic"],"createdAt":"2026-01-02T03:04:05.0000000+00:00"}""",
                await response.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task BindsQueryParameters()
        {
            var all = await Json(await client.GetAsync("/api/inventory/products"));
            var music = await Json(await client.GetAsync("/api/inventory/products?category=music"));
            var named = await Json(await client.GetAsync("/api/inventory/products?text=OUTER"));

            Assert.Equal(3, all.GetArrayLength());
            Assert.Equal("Kind of Blue", music.EnumerateArray().Single().GetProperty("name").GetString());
            Assert.Equal(3, named.EnumerateArray().Single().GetProperty("id").GetInt32());
        }

        [Fact]
        public async Task PostsASingleMessageAsTheBody()
        {
            var response = await client.PostAsync("/api/inventory/products",
                new StringContent("""{"id":10,"name":"Neuromancer","price":"8.5","category":"Books","tags":["cyberpunk"]}""", Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("Neuromancer", (await Json(await client.GetAsync("/api/inventory/products/10"))).GetProperty("name").GetString());
        }

        [Fact]
        public async Task OperationsWithoutAttributesArePostWithAParameterObject()
        {
            var first = await client.PostAsJsonAsync("/api/inventory/AddStock", new { productId = 1, quantity = 4 });
            var second = await client.PostAsJsonAsync("/api/inventory/addstock", new { ProductId = 1, Quantity = 6 });

            Assert.Equal("4", await first.Content.ReadAsStringAsync());
            Assert.Equal("10", await second.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task OperationsWithoutResultAnswer204()
        {
            var deleted = await client.DeleteAsync("/api/inventory/products/2");
            var again = await client.DeleteAsync("/api/inventory/products/2");

            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        }

        [Fact]
        public async Task RoundTripsEveryKindOfValue()
        {
            var sample = Samples.Full();
            var service = ChrysalisRegistry.GetService<IInventoryService>();
            var type = service.FindOperation("Echo")!.ReturnType!;

            using var body = new MemoryStream();
            using (var writer = new Utf8JsonWriter(body, Butterfly.Serialization.Json.JsonFormat.WriterOptions(Butterfly.Serialization.SerializationProfile.Default)))
                Butterfly.Serialization.Json.JsonFormat.Instance.Write(writer, type, sample);
            var response = await client.PostAsync("/api/inventory/Echo", new ByteArrayContent(body.ToArray()) { Headers = { ContentType = new("application/json") } });

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Samples.AssertEqual(sample, (Sample)Butterfly.Serialization.Json.JsonFormat.Instance.Read(document.RootElement, type)!);
        }

        [Fact]
        public async Task ErrorsAreProblemDetails()
        {
            var response = await client.GetAsync("/api/inventory/products/99");

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
            var problem = await Json(response);
            Assert.Equal(404, problem.GetProperty("status").GetInt32());
            Assert.Equal("Product 99 does not exist.", problem.GetProperty("detail").GetString());
            Assert.Equal("NotFound", problem.GetProperty("code").GetString());
        }

        [Theory]
        [InlineData("PermissionDenied", HttpStatusCode.Forbidden)]
        [InlineData("Unauthenticated", HttpStatusCode.Unauthorized)]
        [InlineData("AlreadyExists", HttpStatusCode.Conflict)]
        [InlineData("Unavailable", HttpStatusCode.ServiceUnavailable)]
        [InlineData("Internal", HttpStatusCode.InternalServerError)]
        public async Task StatusesMapToHttp(string status, HttpStatusCode expected)
        {
            var response = await client.PostAsJsonAsync("/api/inventory/Fail", new { status, message = "secret" });

            Assert.Equal(expected, response.StatusCode);
            var detail = (await Json(response)).GetProperty("detail").GetString();
            Assert.Equal(status == "Internal" ? "An internal error occurred." : "secret", detail);
        }

        [Theory]
        [InlineData("GET", "/api/inventory/products/abc", null, HttpStatusCode.BadRequest, "'id': 'abc' is not a valid int32.")]
        [InlineData("GET", "/api/inventory/products?category=Movies", null, HttpStatusCode.BadRequest, "'category': 'Movies' is not a valid Category.")]
        [InlineData("POST", "/api/inventory/products", "{bad json", HttpStatusCode.BadRequest, null)]
        [InlineData("POST", "/api/inventory/products", "", HttpStatusCode.BadRequest, "The request body (product) is required.")]
        [InlineData("POST", "/api/inventory/AddStock", """{"quantity":"many"}""", HttpStatusCode.BadRequest, "'quantity': expected a 32-bit integer.")]
        [InlineData("GET", "/api/inventory/nothing", null, HttpStatusCode.NotFound, null)]
        public async Task RejectsBadRequests(string method, string path, string? body, HttpStatusCode status, string? detail)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            if (body is not null)
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            var response = await client.SendAsync(request);

            Assert.Equal(status, response.StatusCode);
            if (detail is not null)
                Assert.Equal(detail, (await Json(response)).GetProperty("detail").GetString());
        }

        [Fact]
        public async Task WrongMethodsAre405WithAllow()
        {
            var response = await client.PutAsync("/api/inventory/products/1", new StringContent("{}"));

            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
            Assert.Equal(["DELETE", "GET"], response.Content.Headers.Allow.Order());
        }

        [Fact]
        public async Task OnlyJsonBodiesAreAccepted()
        {
            var response = await client.PostAsync("/api/inventory/products", new StringContent("<product/>", Encoding.UTF8, "application/xml"));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task MiddlewareSeesTheHttpRequest()
        {
            chrysalis.Use((context, next) =>
            {
                if (context.Headers.TryGetValue("Authorization", out var authorization) && authorization.StartsWith("Bearer "))
                    context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, authorization[7..])], "bearer"));
                Assert.NotNull(context.GetHttpContext());
                return next(context);
            });
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/inventory/WhoAmI") { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            request.Headers.Add("Authorization", "Bearer ada");

            var response = await client.SendAsync(request);

            Assert.Equal("\"REST:ada\"", await response.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task WorksOverHttp2()
        {
            using var http2 = TestServers.Client(http, http2: true);

            var response = await http2.GetAsync("/api/inventory/products/3");

            Assert.Equal(HttpVersion.Version20, response.Version);
            Assert.Equal("Outer Wilds", (await Json(response)).GetProperty("name").GetString());
        }
    }
}

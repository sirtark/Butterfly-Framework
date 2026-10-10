using Butterfly.Chrysalis.Http;
using Butterfly.Chrysalis.Tests;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Butterfly.Chrysalis.Rest.Tests
{
    // The QUERY method (RFC 10008) over Butterfly's own server, on HTTP/1.1 and HTTP/2.
    public class QueryTests : IAsyncLifetime
    {
        private static readonly HttpMethod Query = new("QUERY");

        private HttpServer http = null!;

        public Task InitializeAsync()
        {
            (http, _) = TestServers.StartChrysalis((http, chrysalis) => http.MapRest(chrysalis, "/api"));
            return Task.CompletedTask;
        }

        public async Task DisposeAsync() => await http.StopAsync();

        private static HttpRequestMessage Request(HttpMethod method, string path, string? json = null, bool http2 = false) => new(method, path)
        {
            Content = json is null ? null : new StringContent(json, Encoding.UTF8, "application/json"),
            Version = http2 ? HttpVersion.Version20 : HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };

        private static async Task<int[]> Ids(HttpResponseMessage response) =>
            [.. JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.EnumerateArray().Select(product => product.GetProperty("id").GetInt32())];

        private static string? AcceptQuery(HttpResponseMessage response) =>
            response.Headers.TryGetValues("Accept-Query", out var values) ? string.Join(", ", values) : null;

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task BindsTheBodyAndTheQueryString(bool http2)
        {
            using var client = TestServers.Client(http, http2);

            using var books = await client.SendAsync(Request(Query, "/api/inventory/search", """{"categories":["Books","Games"]}""", http2));
            using var first = await client.SendAsync(Request(Query, "/api/inventory/search?limit=1", """{"categories":["Books","Games"]}""", http2));
            using var cheap = await client.SendAsync(Request(Query, "/api/inventory/search", """{"maxPrice":15,"text":"o"}""", http2));

            Assert.Equal(http2 ? HttpVersion.Version20 : HttpVersion.Version11, books.Version);
            Assert.Equal(HttpStatusCode.OK, books.StatusCode);
            Assert.Equal(new[] { 1, 3 }, await Ids(books));
            Assert.Equal(new[] { 1 }, await Ids(first));
            Assert.Equal(new[] { 2 }, await Ids(cheap));
            Assert.Equal("application/json", AcceptQuery(books));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task OtherMethodsGet405WithQueryInAllow(bool http2)
        {
            using var client = TestServers.Client(http, http2);

            using var get = await client.SendAsync(Request(HttpMethod.Get, "/api/inventory/search", http2: http2));
            using var post = await client.SendAsync(Request(HttpMethod.Post, "/api/inventory/search", "{}", http2));

            Assert.Equal(HttpStatusCode.MethodNotAllowed, get.StatusCode);
            Assert.Equal(["QUERY"], get.Content.Headers.Allow);
            Assert.Equal("application/json", AcceptQuery(get));
            Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
        }

        [Fact]
        public async Task OptionsAdvertisesQuery()
        {
            using var client = TestServers.Client(http);

            using var response = await client.SendAsync(Request(HttpMethod.Options, "/api/inventory/search"));

            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            Assert.Equal(["OPTIONS", "QUERY"], response.Content.Headers.Allow.Order());
            Assert.Equal("application/json", AcceptQuery(response));
        }

        [Fact]
        public async Task OnlyPathsWithQueryAdvertiseIt()
        {
            using var client = TestServers.Client(http);

            using var product = await client.GetAsync("/api/inventory/products/1");
            using var options = await client.SendAsync(Request(HttpMethod.Options, "/api/inventory/products/1"));

            Assert.Null(AcceptQuery(product));
            Assert.Null(AcceptQuery(options));
            Assert.Equal(["DELETE", "GET", "OPTIONS"], options.Content.Headers.Allow.Order());
        }

        [Fact]
        public async Task QueryContentMustBeTypedJson()
        {
            using var client = TestServers.Client(http);

            // RFC 10008: content without a Content-Type is rejected, and an unsupported media type is a 415.
            using var untyped = await client.SendAsync(new HttpRequestMessage(Query, "/api/inventory/search") { Content = new ByteArrayContent("{}"u8.ToArray()) });
            using var xml = await client.SendAsync(new HttpRequestMessage(Query, "/api/inventory/search") { Content = new StringContent("<filter/>", Encoding.UTF8, "application/xml") });
            using var missing = await client.SendAsync(Request(Query, "/api/inventory/search"));

            Assert.Equal(HttpStatusCode.BadRequest, untyped.StatusCode);
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, xml.StatusCode);
            Assert.Equal("application/json", AcceptQuery(xml));
            Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
            Assert.Equal("The request body (filter) is required.", JsonDocument.Parse(await missing.Content.ReadAsStringAsync()).RootElement.GetProperty("detail").GetString());
        }
    }
}

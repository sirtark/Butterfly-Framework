using Butterfly.Chrysalis.Tests;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;

namespace Butterfly.Chrysalis.Http.Tests
{
    public class HttpServerTests
    {
        // Echoes method, path, query, a header and the body.
        private static void MapEcho(HttpServer server) => server.Map("/", async context =>
        {
            await Task.Yield();
            var request = context.Request;
            var text = $"{request.Method} {request.Path} q={string.Join(",", request.Query["q"])} {request.Protocol} h={request.Headers["X-Test"]} body={Encoding.UTF8.GetString(request.Body.Span)}";
            context.Response.Headers["X-Echo"] = "yes";
            context.Response.Write(text, "text/plain; charset=utf-8");
        });

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task ServesHttp1AndHttp2WithAndWithoutTls(bool http2, bool tls)
        {
            await using var server = TestServers.StartHttp(MapEcho, tls);
            using var client = TestServers.Client(server, http2);
            using var request = new HttpRequestMessage(HttpMethod.Post, "/items/a%20b?q=1&q=two+words")
            {
                Content = new StringContent("héllo"),
                // A hand-built request does not take the client defaults.
                Version = client.DefaultRequestVersion,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact
            };
            request.Headers.Add("X-Test", "value");

            using var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(http2 ? HttpVersion.Version20 : HttpVersion.Version11, response.Version);
            Assert.Equal("yes", response.Headers.GetValues("X-Echo").Single());
            Assert.Equal("Butterfly.Chrysalis", response.Headers.Server.ToString());
            Assert.Equal($"POST /items/a b q=1,two words {(http2 ? "HTTP/2" : "HTTP/1.1")} h=value body=héllo", await response.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task RoutesToTheLongestPrefixAndConditionsFirst()
        {
            await using var server = TestServers.StartHttp(server => server
                .Map("/", context => { context.Response.Write("root", "text/plain"); return default; })
                .Map("/api", context => { context.Response.Write("api", "text/plain"); return default; })
                .Map("/api/v2", context => { context.Response.Write("v2", "text/plain"); return default; })
                .MapWhen(request => request.Headers["X-Special"] is not null, new Handler("special")));
            using var client = TestServers.Client(server);

            Assert.Equal("root", await client.GetStringAsync("/apix"));
            Assert.Equal("api", await client.GetStringAsync("/api"));
            Assert.Equal("api", await client.GetStringAsync("/API/v1/x"));
            Assert.Equal("v2", await client.GetStringAsync("/api/v2/x"));
            using var special = new HttpRequestMessage(HttpMethod.Get, "/api/v2");
            special.Headers.Add("X-Special", "1");
            Assert.Equal("special", await (await client.SendAsync(special)).Content.ReadAsStringAsync());
        }

        private sealed class Handler(string text) : IHttpHandler
        {
            public ValueTask HandleAsync(HttpServerContext context)
            {
                context.Response.Write(text, "text/plain");
                return default;
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task UnmappedPathsAre404AndFailingHandlers500(bool http2)
        {
            await using var server = TestServers.StartHttp(server => server
                .Map("/fail", _ => throw new InvalidOperationException("boom")));
            using var client = TestServers.Client(server, http2);

            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/missing")).StatusCode);
            var failed = await client.GetAsync("/fail");
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
            Assert.DoesNotContain("boom", await failed.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task LargeBodiesFlowBothWaysOverHttp2()
        {
            // Bigger than every flow-control window, so WINDOW_UPDATE frames must keep both directions moving.
            var payload = new byte[3 * 1024 * 1024 + 17];
            Random.Shared.NextBytes(payload);
            await using var server = TestServers.StartHttp(server => server.Map("/", context =>
            {
                context.Response.Write(context.Request.Body.Span);
                context.Response.Write(context.Request.Body.Span);
                return default;
            }));
            using var client = TestServers.Client(server, http2: true);

            using var response = await client.PostAsync("/", new ByteArrayContent(payload));
            var echoed = await response.Content.ReadAsByteArrayAsync();

            Assert.Equal(payload.Length * 2, echoed.Length);
            Assert.True(echoed.AsSpan(0, payload.Length).SequenceEqual(payload));
            Assert.True(echoed.AsSpan(payload.Length).SequenceEqual(payload));
        }

        [Fact]
        public async Task ConcurrentStreamsShareOneHttp2Connection()
        {
            var connections = new HashSet<string>();
            await using var server = TestServers.StartHttp(server => server.Map("/", async context =>
            {
                lock (connections)
                    connections.Add(context.Request.RemoteAddress);
                await Task.Delay(Random.Shared.Next(1, 30));
                context.Response.Write(context.Request.Path, "text/plain");
            }));
            using var client = TestServers.Client(server, http2: true);

            var results = await Task.WhenAll(Enumerable.Range(0, 60).Select(i => client.GetStringAsync($"/r{i}")));

            Assert.Equal(Enumerable.Range(0, 60).Select(i => $"/r{i}"), results);
            Assert.Single(connections);
        }

        [Fact]
        public async Task Http2SendsTrailers()
        {
            await using var server = TestServers.StartHttp(server => server.Map("/", context =>
            {
                context.Response.Write("body", "text/plain");
                context.Response.Trailers.Add("x-checksum", "42");
                return default;
            }));
            using var client = TestServers.Client(server, http2: true);

            using var response = await client.GetAsync("/");
            Assert.Equal("body", await response.Content.ReadAsStringAsync());

            Assert.Equal("42", response.TrailingHeaders.GetValues("x-checksum").Single());
        }

        [Fact]
        public async Task Http2RejectsOversizedBodies()
        {
            await using var server = TestServers.StartHttp(MapEcho, configure: options => options.MaxRequestBodySize = 1000);
            using var client = TestServers.Client(server, http2: true);

            using var response = await client.PostAsync("/", new ByteArrayContent(new byte[5000]));

            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
            // The connection survives.
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);
        }

        [Fact]
        public async Task HandlersSeeCancellationWhenTheServerStops()
        {
            var started = new TaskCompletionSource();
            var cancelled = new TaskCompletionSource();
            var server = TestServers.StartHttp(server => server.Map("/", async context =>
            {
                started.SetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, context.RequestAborted);
                }
                catch (OperationCanceledException)
                {
                    cancelled.SetResult();
                }
            }));
            using var client = TestServers.Client(server, http2: true);

            var call = client.GetAsync("/");
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await server.StopAsync();

            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // Whether the client still gets the (empty) response depends on timing: only the cancellation is guaranteed.
            try { await call; } catch (HttpRequestException) { }
            Assert.False(server.IsRunning);
        }

        [Fact]
        public async Task EndpointsReportTheRealPort()
        {
            await using var server = TestServers.StartHttp(MapEcho);

            Assert.NotEqual(0, server.Port());
            Assert.True(server.IsRunning);
            Assert.Throws<InvalidOperationException>(server.Start);
        }

        [Fact]
        public void HeadersRejectResponseSplitting()
        {
            var headers = new HttpServerHeaders();

            Assert.Throws<ArgumentException>(() => headers.Add("X-Bad", "a\r\nSet-Cookie: x=1"));
            Assert.Throws<ArgumentException>(() => headers.Add("Bad Name", "a"));
            headers.Add("X-Multi", "a");
            headers.Add("x-multi", "b");
            Assert.Equal("a, b", headers["X-MULTI"]);
        }
    }
}

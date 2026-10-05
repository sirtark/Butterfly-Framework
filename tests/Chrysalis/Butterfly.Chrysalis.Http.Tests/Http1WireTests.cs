using Butterfly.Chrysalis.Tests;
using System.Net.Sockets;
using System.Text;

namespace Butterfly.Chrysalis.Http.Tests
{
    // HTTP/1.1 written by hand, for what clients normally hide: framing, limits and malformed input.
    public class Http1WireTests : IAsyncLifetime
    {
        private HttpServer server = null!;

        public Task InitializeAsync()
        {
            server = TestServers.StartHttp(server => server.Map("/", context =>
            {
                context.Response.Write($"{context.Request.Method} {context.Request.Target} [{Encoding.UTF8.GetString(context.Request.Body.Span)}]", "text/plain");
                return default;
            }), configure: options =>
            {
                options.MaxRequestBodySize = 100;
                options.MaxRequestHeadersSize = 512;
                options.RequestTimeout = TimeSpan.FromSeconds(1);
            });
            return Task.CompletedTask;
        }

        public Task DisposeAsync() => server.StopAsync();

        // Sends raw bytes and returns everything the server answers until it closes (or a short silence).
        private async Task<string> Exchange(string request, int closeAfterResponses = 0)
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync("127.0.0.1", server.Port());
            var stream = tcp.GetStream();
            await stream.WriteAsync(Encoding.Latin1.GetBytes(request));
            stream.ReadTimeout = 3000;

            var received = new MemoryStream();
            var buffer = new byte[8192];
            try
            {
                int read;
                while ((read = await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(2))) > 0)
                {
                    received.Write(buffer, 0, read);
                    if (closeAfterResponses > 0 && Count(Encoding.Latin1.GetString(received.ToArray()), "HTTP/1.1 ") >= closeAfterResponses)
                        break;
                }
            }
            catch (Exception exception) when (exception is TimeoutException or IOException)
            {
            }
            return Encoding.Latin1.GetString(received.ToArray());
        }

        private static int Count(string text, string value) => (text.Length - text.Replace(value, "").Length) / value.Length;

        [Fact]
        public async Task KeepsTheConnectionAliveAndPipelines()
        {
            var answer = await Exchange("GET /a HTTP/1.1\r\nHost: x\r\n\r\nGET /b HTTP/1.1\r\nHost: x\r\n\r\n", closeAfterResponses: 2);

            Assert.Equal(2, Count(answer, "HTTP/1.1 200 OK"));
            Assert.Contains("GET /a []", answer);
            Assert.Contains("GET /b []", answer);
            Assert.Contains("Content-Length: 9", answer);
        }

        [Fact]
        public async Task ReadsChunkedBodies()
        {
            var answer = await Exchange("POST /c HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n5;ext=1\r\nhello\r\n6\r\n world\r\n0\r\nX-Trailer: t\r\n\r\n");

            Assert.StartsWith("HTTP/1.1 200 OK", answer);
            Assert.Contains("POST /c [hello world]", answer);
            Assert.Contains("Connection: close", answer);
        }

        [Fact]
        public async Task AnswersExpectContinue()
        {
            var answer = await Exchange("POST /e HTTP/1.1\r\nHost: x\r\nExpect: 100-continue\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");

            Assert.StartsWith("HTTP/1.1 100 Continue\r\n\r\nHTTP/1.1 200 OK", answer);
        }

        [Fact]
        public async Task Http10ClosesUnlessAskedToKeepAlive()
        {
            var closed = await Exchange("GET /old HTTP/1.0\r\n\r\n");
            Assert.StartsWith("HTTP/1.1 200 OK", closed);
            Assert.Contains("Connection: close", closed);

            var kept = await Exchange("GET /old HTTP/1.0\r\nConnection: keep-alive\r\n\r\n", closeAfterResponses: 1);
            Assert.Contains("Connection: keep-alive", kept);
        }

        [Fact]
        public async Task HeadSendsNoBody()
        {
            var answer = await Exchange("HEAD /h HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n");

            // The length of the body a GET would get ("HEAD /h []"), but no body.
            Assert.Contains("Content-Length: 10", answer);
            Assert.EndsWith("\r\n\r\n", answer);
        }

        [Theory]
        [InlineData("GET /x HTTP/1.1\r\nHost: x\r\nContent-Length: 3\r\nTransfer-Encoding: chunked\r\n\r\n", "400")]   // smuggling
        [InlineData("GET /x HTTP/1.1\r\nHost: x\r\nContent-Length: 3\r\nContent-Length: 4\r\n\r\n", "400")]
        [InlineData("GET /x HTTP/1.1\r\nHost: x\r\nContent-Length: -1\r\n\r\n", "400")]
        [InlineData("GET /x HTTP/1.1\r\nHost : x\r\n\r\n", "400")]                                              // space before colon
        [InlineData("GET /x HTTP/1.1\r\nHost: x\r\nX-A: a\r\n folded\r\n\r\n", "400")]                          // obs-fold
        [InlineData("GET /x HTTP/1.1\r\n\r\n", "400")]                                                          // no Host
        [InlineData("GET /x\r\n\r\n", "400")]
        [InlineData("GET /x HTTP/3.0\r\nHost: x\r\n\r\n", "505")]
        [InlineData("POST /x HTTP/1.1\r\nHost: x\r\nContent-Length: 101\r\n\r\n", "413")]
        [InlineData("POST /x HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: gzip\r\n\r\n", "501")]
        [InlineData("POST /x HTTP/1.1\r\nHost: x\r\nExpect: magic\r\nContent-Length: 1\r\n\r\nx", "417")]
        public async Task RejectsMalformedAndDangerousRequests(string request, string status)
        {
            var answer = await Exchange(request);

            Assert.StartsWith($"HTTP/1.1 {status} ", answer);
            Assert.Contains("Connection: close", answer);
        }

        [Fact]
        public async Task LimitsChunkedBodiesToo()
        {
            var answer = await Exchange("POST /x HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\n\r\n80\r\n" + new string('a', 128) + "\r\n0\r\n\r\n");

            Assert.StartsWith("HTTP/1.1 413 ", answer);
        }

        [Fact]
        public async Task LimitsHeaders()
        {
            Assert.StartsWith("HTTP/1.1 431 ", await Exchange("GET /x HTTP/1.1\r\nHost: x\r\nX-Big: " + new string('a', 600) + "\r\n\r\n"));
            Assert.StartsWith("HTTP/1.1 414 ", await Exchange("GET /" + new string('a', 9000) + " HTTP/1.1\r\nHost: x\r\n\r\n"));
        }

        [Fact]
        public async Task SlowClientsAreDisconnected()
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync("127.0.0.1", server.Port());
            var stream = tcp.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: x\r\n"));

            // The headers never end; the server gives up after RequestTimeout (1 s).
            var buffer = new byte[256];
            var read = await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(0, read);
        }

        [Fact]
        public async Task AcceptsAbsoluteFormTargets()
        {
            var answer = await Exchange("GET http://example.com/abs?x=1 HTTP/1.1\r\nHost: example.com\r\nConnection: close\r\n\r\n");

            Assert.Contains("GET /abs?x=1 []", answer);
        }
    }
}

using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;

using Butterfly.Communication.Testing;

namespace Butterfly.Communication.Http.Tests
{
    public class HttpClientTests
    {
        [Fact]
        public void GetSendsWellFormedRequest()
        {
            ReceivedRequest? received = null;
            using var server = new TestServer(session =>
            {
                received = ReadRequest(session);
                Respond(session, 200, "hola mundo", "text/plain; charset=utf-8");
            });

            using var client = new HttpClient();
            using HttpResponse response = client.Get($"http://localhost:{server.Port}/ruta?q=1%202");
            server.Wait();

            Assert.Equal(200, response.StatusCode);
            Assert.Equal("hola mundo", response.ReadAsString());
            Assert.Equal("GET /ruta?q=1%202 HTTP/1.1", received!.RequestLine);
            Assert.Equal($"localhost:{server.Port}", received.Headers["host"]);
            Assert.StartsWith("Butterfly/", received.Headers["user-agent"]);
            Assert.Equal("gzip, deflate, br", received.Headers["accept-encoding"]);
        }

        [Fact]
        public void DecodesChunkedBodiesWithExtensionsAndTrailers()
        {
            using var server = new TestServer(session =>
            {
                ReadRequest(session);
                session.Write("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n" +
                              "5;ext=1\r\nhola \r\n6\r\nchunks\r\n0\r\nX-Trailer: fin\r\n\r\n");
            });

            using var client = new HttpClient();
            Assert.Equal("hola chunks", client.GetString($"http://127.0.0.1:{server.Port}/"));
            server.Wait();
        }

        [Fact]
        public void DecompressesGzip()
        {
            using var server = new TestServer(session =>
            {
                ReadRequest(session);
                byte[] body = Gzip("comprimido ".PadRight(500, 'x'));
                session.Write($"HTTP/1.1 200 OK\r\nContent-Encoding: gzip\r\nContent-Length: {body.Length}\r\n\r\n");
                session.Write(body);
            });

            using var client = new HttpClient();
            Assert.Equal("comprimido ".PadRight(500, 'x'), client.GetString($"http://127.0.0.1:{server.Port}/"));
            server.Wait();
        }

        [Fact]
        public void ReusesKeepAliveConnections()
        {
            using var server = new TestServer(session =>
            {
                for (int i = 0; i < 3; i++)
                {
                    ReadRequest(session);
                    Respond(session, 200, $"respuesta {i}");
                }
            });

            using var client = new HttpClient();
            for (int i = 0; i < 3; i++)
                Assert.Equal($"respuesta {i}", client.GetString($"http://127.0.0.1:{server.Port}/{i}"));

            server.Wait(); // A single scripted connection served all three.
        }

        [Fact]
        public void RetriesWhenAPooledConnectionWasClosed()
        {
            using var server = new TestServer((session, index) =>
            {
                ReadRequest(session);
                Respond(session, 200, $"conexion {index}");
                // The first connection closes right after answering, without announcing it.
            }, connections: 2);

            using var client = new HttpClient();
            Assert.Equal("conexion 0", client.GetString($"http://127.0.0.1:{server.Port}/"));
            Thread.Sleep(100);
            Assert.Equal("conexion 1", client.GetString($"http://127.0.0.1:{server.Port}/"));
            server.Wait();
        }

        [Fact]
        public void FollowsRedirectsAndDropsCredentialsAcrossOrigins()
        {
            ReceivedRequest? final = null;
            using var target = new TestServer(session =>
            {
                final = ReadRequest(session);
                Respond(session, 200, "destino");
            });

            using var origin = new TestServer(session =>
            {
                ReadRequest(session);
                session.Write($"HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:{target.Port}/final\r\nContent-Length: 0\r\n\r\n");
            });

            using var client = new HttpClient();
            var request = HttpRequest.Post($"http://localhost:{origin.Port}/inicio", HttpContent.FromString("datos")).SetBearerToken("secreto");
            using HttpResponse response = client.Send(request);

            Assert.Equal("destino", response.ReadAsString());
            Assert.Equal($"http://127.0.0.1:{target.Port}/final", response.RequestUri.ToString());
            Assert.Equal("GET /final HTTP/1.1", final!.RequestLine);
            Assert.False(final.Headers.ContainsKey("authorization"));
            Assert.Equal("", final.Body);
        }

        [Fact]
        public void HttpsWithTrustedCertificate()
        {
            using var server = new TestServer(session =>
            {
                session.StartTls(TestCertificate.Localhost);
                ReadRequest(session);
                Respond(session, 200, "seguro");
            });

            using var client = new HttpClient(new HttpClientOptions { Connection = new ConnectionOptions { Tls = TestCertificate.TrustingOptions } });
            Assert.Equal("seguro", client.GetString($"https://localhost:{server.Port}/"));
            server.Wait();
        }

        [Fact]
        public void PostsFormsAndChunkedStreams()
        {
            var bodies = new List<ReceivedRequest>();
            using var server = new TestServer(session =>
            {
                for (int i = 0; i < 2; i++)
                {
                    bodies.Add(ReadRequest(session));
                    Respond(session, 204, "");
                }
            });

            using var client = new HttpClient();
            client.Post($"http://127.0.0.1:{server.Port}/form", HttpContent.FromForm(("nombre", "Ana María"), ("x", "a&b")));

            using var unknownLength = new NonSeekableStream(Encoding.UTF8.GetBytes("flujo sin longitud"));
            client.Post($"http://127.0.0.1:{server.Port}/stream", HttpContent.FromStream(unknownLength));
            server.Wait();

            Assert.Equal("application/x-www-form-urlencoded", bodies[0].Headers["content-type"]);
            Assert.Equal("nombre=Ana+Mar%C3%ADa&x=a%26b", bodies[0].Body);
            Assert.Equal("chunked", bodies[1].Headers["transfer-encoding"]);
            Assert.Equal("flujo sin longitud", bodies[1].Body);
        }

        [Fact]
        public void UploadsMultipartForms()
        {
            ReceivedRequest? received = null;
            using var server = new TestServer(session =>
            {
                received = ReadRequest(session);
                Respond(session, 200, "ok");
            });

            var form = new MultipartFormContent()
                .Add("titulo", "informe")
                .AddFile("archivo", "datos.csv", Encoding.UTF8.GetBytes("a,b\n1,2"), "text/csv");

            using var client = new HttpClient();
            client.Post($"http://127.0.0.1:{server.Port}/subir", form).EnsureSuccessStatusCode();
            server.Wait();

            string boundary = received!.Headers["content-type"].Split("boundary=")[1];
            Assert.Equal(form.Length, long.Parse(received.Headers["content-length"], CultureInfo.InvariantCulture));
            Assert.Contains($"--{boundary}\r\nContent-Disposition: form-data; name=\"titulo\"\r\n\r\ninforme\r\n", received.Body);
            Assert.Contains("filename=\"datos.csv\"\r\nContent-Type: text/csv\r\n\r\na,b\n1,2\r\n", received.Body);
            Assert.EndsWith($"--{boundary}--\r\n", received.Body);
        }

        [Fact]
        public void KeepsCookies()
        {
            ReceivedRequest? second = null;
            using var server = new TestServer(session =>
            {
                ReadRequest(session);
                session.Write("HTTP/1.1 200 OK\r\nSet-Cookie: sesion=abc123; Path=/\r\nContent-Length: 0\r\n\r\n");
                second = ReadRequest(session);
                Respond(session, 200, "");
            });

            using var client = new HttpClient(new HttpClientOptions { Cookies = new CookieContainer() });
            client.Get($"http://127.0.0.1:{server.Port}/login");
            client.Get($"http://127.0.0.1:{server.Port}/perfil");
            server.Wait();

            Assert.Equal("sesion=abc123", second!.Headers["cookie"]);
        }

        [Fact]
        public void SkipsInterimResponsesAndReadsUntilClose()
        {
            using var server = new TestServer(session =>
            {
                ReadRequest(session);
                session.Write("HTTP/1.1 100 Continue\r\n\r\nHTTP/1.0 200 OK\r\n\r\ncuerpo hasta el cierre");
            });

            using var client = new HttpClient();
            using HttpResponse response = client.Get($"http://127.0.0.1:{server.Port}/");
            Assert.Equal(new Version(1, 0), response.Version);
            Assert.Equal("cuerpo hasta el cierre", response.ReadAsString());
        }

        [Fact]
        public void StreamsLargeBodies()
        {
            byte[] payload = new byte[3 * 1024 * 1024];
            Random.Shared.NextBytes(payload);

            using var server = new TestServer(session =>
            {
                ReadRequest(session);
                session.Write($"HTTP/1.1 200 OK\r\nContent-Length: {payload.Length}\r\n\r\n");
                session.Write(payload);
            });

            using var client = new HttpClient(new HttpClientOptions { AutomaticDecompression = false });
            using HttpResponse response = client.Send(HttpRequest.Get($"http://127.0.0.1:{server.Port}/grande"), HttpCompletion.Streamed);
            using var copy = new MemoryStream();
            response.Body.CopyTo(copy);

            Assert.Equal(payload, copy.ToArray());
        }

        [Fact]
        public void FailedStatusCarriesTheResponse()
        {
            using var server = new TestServer(session =>
            {
                ReadRequest(session);
                Respond(session, 404, "no existe");
            });

            using var client = new HttpClient();
            var exception = Assert.Throws<HttpStatusException>(() => client.GetString($"http://127.0.0.1:{server.Port}/nada"));
            Assert.Equal(404, exception.StatusCode);
            Assert.Equal("no existe", exception.Response.ReadAsString());
        }

        [Fact]
        public void RejectsConflictingContentLengths()
        {
            using var server = new TestServer(session =>
            {
                ReadRequest(session);
                session.Write("HTTP/1.1 200 OK\r\nContent-Length: 5\r\nContent-Length: 50\r\n\r\nhola!");
            });

            using var client = new HttpClient();
            Assert.Throws<HttpException>(() => client.Get($"http://127.0.0.1:{server.Port}/"));
        }

        [Fact]
        public void TimesOutSilentServers()
        {
            using var server = new TestServer(session => { ReadRequest(session); Thread.Sleep(2000); });

            using var client = new HttpClient(new HttpClientOptions { Timeout = TimeSpan.FromMilliseconds(300) });
            Assert.Throws<TimeoutException>(() => client.Get($"http://127.0.0.1:{server.Port}/"));
        }

        [Fact]
        public async Task SupportsAsyncAndCancellation()
        {
            using var server = new TestServer(session => { ReadRequest(session); Thread.Sleep(2000); });
            using var client = new HttpClient();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

            var watch = System.Diagnostics.Stopwatch.StartNew();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetStringAsync($"http://127.0.0.1:{server.Port}/", cancellation.Token));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1.5), $"Cancelling took {watch.Elapsed}.");
        }

        [Fact]
        public void RejectsHeaderInjection()
        {
            var request = HttpRequest.Get("http://example.com/");
            Assert.Throws<ArgumentException>(() => request.Headers.Add("X-Test", "valor\r\nInjected: si"));
            Assert.Throws<ArgumentException>(() => request.Headers.Add("Mal Nombre", "valor"));
        }

        // --- helpers -----------------------------------------------------------------------------------------

        internal sealed record ReceivedRequest(string RequestLine, Dictionary<string, string> Headers, string Body);

        internal static ReceivedRequest ReadRequest(ServerSession session)
        {
            string requestLine = session.ReadLine();
            var headers = new Dictionary<string, string>();
            foreach (string line in session.ReadUntilEmptyLine())
            {
                int colon = line.IndexOf(':');
                headers[line[..colon].Trim().ToLowerInvariant()] = line[(colon + 1)..].Trim();
            }

            var body = new MemoryStream();
            if (headers.TryGetValue("content-length", out string? length))
            {
                body.Write(session.ReadBytes(int.Parse(length, CultureInfo.InvariantCulture)));
            }
            else if (headers.TryGetValue("transfer-encoding", out string? coding) && coding == "chunked")
            {
                for (int size; (size = int.Parse(session.ReadLine(), NumberStyles.HexNumber, CultureInfo.InvariantCulture)) > 0;)
                {
                    body.Write(session.ReadBytes(size));
                    session.ReadLine();
                }
                session.ReadLine();
            }

            return new ReceivedRequest(requestLine, headers, Encoding.UTF8.GetString(body.ToArray()));
        }

        internal static void Respond(ServerSession session, int status, string body, string contentType = "text/plain")
        {
            byte[] bytes = Encoding.UTF8.GetBytes(body);
            session.Write($"HTTP/1.1 {status} Status\r\nContent-Type: {contentType}\r\nContent-Length: {bytes.Length}\r\n\r\n");
            session.Write(bytes);
        }

        private static byte[] Gzip(string text)
        {
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionLevel.Fastest))
                gzip.Write(Encoding.UTF8.GetBytes(text));
            return output.ToArray();
        }

        private sealed class NonSeekableStream(byte[] data) : MemoryStream(data)
        {
            public override bool CanSeek => false;
        }
    }
}

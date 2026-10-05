using Butterfly.Chrysalis.Http;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Butterfly.Chrysalis.Tests
{
    // Real servers on loopback with a free port, and clients that talk to them.
    internal static class TestServers
    {
        public static X509Certificate2 Certificate => TestCertificate.Value;

        public static HttpServer StartHttp(Action<HttpServer> map, bool tls = false, Action<HttpServerOptions>? configure = null)
        {
            var options = new HttpServerOptions();
            options.Endpoints.Add(HttpEndpoint.Loopback(0, tls ? Certificate : null));
            configure?.Invoke(options);
            var server = new HttpServer(options);
            map(server);
            server.Start();
            return server;
        }

        /// <summary>An HTTP server exposing a fresh InventoryService through the protocols <paramref name="map"/> adds.</summary>
        public static (HttpServer Http, ChrysalisServer Chrysalis) StartChrysalis(Action<HttpServer, ChrysalisServer> map, bool tls = false)
        {
            var chrysalis = new ChrysalisServer().Expose<Inventory.IInventoryService>(new Inventory.InventoryService());
            return (StartHttp(http => map(http, chrysalis), tls), chrysalis);
        }

        public static Uri BaseAddress(this HttpServer server) =>
            new($"{(server.Endpoints[0].IsSecure ? "https" : "http")}://127.0.0.1:{server.Endpoints[0].Address.Port}/");

        public static int Port(this HttpServer server) => server.Endpoints[0].Address.Port;

        /// <summary>HttpClient for HTTP/1.1, or HTTP/2 only (h2c with prior knowledge on http://, ALPN on https://).</summary>
        public static HttpClient Client(HttpServer server, bool http2 = false) => new(new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, _, _, _) => true },
            EnableMultipleHttp2Connections = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(1)
        })
        {
            BaseAddress = server.BaseAddress(),
            DefaultRequestVersion = http2 ? HttpVersion.Version20 : HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Timeout = TimeSpan.FromSeconds(30)
        };
    }
}

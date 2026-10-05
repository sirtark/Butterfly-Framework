using Butterfly.Networking.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Security.Cryptography.X509Certificates;

namespace Butterfly.Chrysalis.Http
{
    /// <summary>An address to listen on; with a certificate it serves HTTPS (HTTP/2 is negotiated with ALPN).</summary>
    public sealed class HttpEndpoint(SocketAddress address, X509Certificate2? certificate = null)
    {
        public SocketAddress Address { get; } = address;
        public X509Certificate2? Certificate { get; } = certificate;
        public bool IsSecure => Certificate is not null;

        /// <summary>All IPv4 interfaces. Port 0 picks a free port (see <see cref="HttpServer.Endpoints"/>).</summary>
        public static HttpEndpoint Any(int port, X509Certificate2? certificate = null) => new(SocketAddress.Any(AddressFamily.IPv4, checked((ushort)port)), certificate);
        public static HttpEndpoint Loopback(int port, X509Certificate2? certificate = null) => new(SocketAddress.Loopback(AddressFamily.IPv4, checked((ushort)port)), certificate);

        public override string ToString() => $"{(IsSecure ? "https" : "http")}://{Address}";
    }

    /// <summary>Limits are on by default: every request is held in memory and served by a thread while it is read.</summary>
    public sealed class HttpServerOptions
    {
        public IList<HttpEndpoint> Endpoints { get; } = [];

        /// <summary>Longest request line (method, target, version). Longer ones get 414.</summary>
        public int MaxRequestLineLength { get; set; } = 8 * 1024;
        /// <summary>Total size of the request headers (HTTP/2: the decoded header list). Bigger ones get 431.</summary>
        public int MaxRequestHeadersSize { get; set; } = 32 * 1024;
        public int MaxRequestHeaderCount { get; set; } = 100;
        /// <summary>Largest request body. Bigger ones get 413.</summary>
        public long MaxRequestBodySize { get; set; } = 8 * 1024 * 1024;
        /// <summary>Connections beyond this are closed as soon as they are accepted.</summary>
        public int MaxConcurrentConnections { get; set; } = 1000;

        /// <summary>How long a client may take to send the headers of a request, and between reads of its body (slow clients).</summary>
        public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);
        /// <summary>How long an idle connection is kept open waiting for the next request.</summary>
        public TimeSpan KeepAliveTimeout { get; set; } = TimeSpan.FromMinutes(2);
        /// <summary>How long a handler may run before the request is cancelled (<see cref="HttpServerContext.RequestAborted"/>).</summary>
        public TimeSpan HandlerTimeout { get; set; } = TimeSpan.FromMinutes(5);

        public bool EnableHttp2 { get; set; } = true;
        public int Http2MaxConcurrentStreams { get; set; } = 100;

        /// <summary>Value of the Server header; null leaves it out.</summary>
        public string? ServerHeader { get; set; } = "Butterfly.Chrysalis";
        public ILogger Logger { get; set; } = NullLogger.Instance;
    }
}

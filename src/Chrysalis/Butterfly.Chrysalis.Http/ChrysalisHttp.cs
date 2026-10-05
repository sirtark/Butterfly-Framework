namespace Butterfly.Chrysalis.Http
{
    /// <summary>What every HTTP-based Chrysalis protocol shares.</summary>
    public static class ChrysalisHttp
    {
        /// <summary>
        /// A call context for an HTTP request: its headers become <see cref="ChrysalisCallContext.Headers"/>, and the
        /// HTTP context stays reachable from middleware through <c>Items[typeof(HttpServerContext)]</c>.
        /// </summary>
        public static ChrysalisCallContext CreateCallContext(ChrysalisOperation operation, object?[] arguments, string protocol, HttpServerContext http, CancellationToken cancellationToken)
        {
            var context = new ChrysalisCallContext(operation, arguments, protocol, cancellationToken)
            {
                RemoteAddress = http.Request.RemoteAddress
            };
            foreach (var (name, value) in http.Request.Headers)
                context.Headers[name] = context.Headers.TryGetValue(name, out var existing) ? existing + ", " + value : value;
            context.Items[typeof(HttpServerContext)] = http;
            return context;
        }

        /// <summary>The HTTP request behind a call, when it arrived over HTTP.</summary>
        public static HttpServerContext? GetHttpContext(this ChrysalisCallContext context) =>
            context.Items.TryGetValue(typeof(HttpServerContext), out var http) ? http as HttpServerContext : null;

        /// <summary>The absolute URL of the server as the client sees it ("https://host:port").</summary>
        public static string BaseUrl(HttpServerRequest request) =>
            $"{(request.IsSecure ? "https" : "http")}://{request.Host ?? "localhost"}";
    }
}

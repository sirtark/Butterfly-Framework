using System.Buffers;
using System.Collections;
using System.Text;

namespace Butterfly.Chrysalis.Http
{
    /// <summary>Header fields in arrival order. Names are case-insensitive; a name may appear more than once.</summary>
    public sealed class HttpServerHeaders : IEnumerable<KeyValuePair<string, string>>
    {
        private readonly List<KeyValuePair<string, string>> fields = [];

        public int Count => fields.Count;

        /// <summary>The value of a header, multiple occurrences joined with ", " (RFC 9110 5.3), or null.</summary>
        public string? this[string name]
        {
            get
            {
                string? joined = null;
                foreach (var (key, value) in fields)
                {
                    if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                        joined = joined is null ? value : joined + ", " + value;
                }
                return joined;
            }
            set
            {
                Remove(name);
                if (value is not null)
                    Add(name, value);
            }
        }

        public IEnumerable<string> GetValues(string name) =>
            fields.Where(field => string.Equals(field.Key, name, StringComparison.OrdinalIgnoreCase)).Select(field => field.Value);

        public bool Contains(string name) => fields.Exists(field => string.Equals(field.Key, name, StringComparison.OrdinalIgnoreCase));

        /// <exception cref="ArgumentException">The name is not a valid token or the value contains a line break (response splitting).</exception>
        public void Add(string name, string value)
        {
            if (name.Length == 0 || !name.All(IsTokenChar))
                throw new ArgumentException($"'{name}' is not a valid header name.", nameof(name));
            if (value.AsSpan().IndexOfAny('\r', '\n', '\0') >= 0)
                throw new ArgumentException($"The value of the header '{name}' contains a line break.", nameof(value));
            fields.Add(new(name, value));
        }

        public bool Remove(string name) => fields.RemoveAll(field => string.Equals(field.Key, name, StringComparison.OrdinalIgnoreCase)) > 0;

        public void Clear() => fields.Clear();

        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => fields.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        internal void AddUnchecked(string name, string value) => fields.Add(new(name, value));

        internal static bool IsTokenChar(char c) =>
            c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9'
            or '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~';
    }

    public sealed class HttpServerRequest
    {
        /// <summary>Builds a request; used by the Chrysalis HTTP server and by hosting adapters (ASP.NET Core).</summary>
        public HttpServerRequest(string method, string target, string protocol, bool isSecure, string remoteAddress, HttpServerHeaders headers, ReadOnlyMemory<byte> body)
        {
            Method = method;
            Target = target;
            Protocol = protocol;
            IsSecure = isSecure;
            RemoteAddress = remoteAddress;
            Headers = headers;
            Body = body;

            var query = target.IndexOf('?');
            var rawPath = query < 0 ? target : target[..query];
            RawPath = rawPath;
            QueryString = query < 0 ? string.Empty : target[(query + 1)..];
            Path = Uri.UnescapeDataString(rawPath);
            Query = ParseQuery(QueryString);
        }

        public string Method { get; }
        /// <summary>The request target as sent: path and query, still percent-encoded.</summary>
        public string Target { get; }
        public string RawPath { get; }
        /// <summary>The path, percent-decoded.</summary>
        public string Path { get; }
        public string QueryString { get; }
        /// <summary>Decoded query parameters; a name may have several values.</summary>
        public ILookup<string, string> Query { get; }
        /// <summary>"HTTP/1.1", "HTTP/1.0" or "HTTP/2".</summary>
        public string Protocol { get; }
        public bool IsSecure { get; }
        public string RemoteAddress { get; }
        public HttpServerHeaders Headers { get; }
        /// <summary>The whole body (requests are read completely before the handler runs).</summary>
        public ReadOnlyMemory<byte> Body { get; }

        public string? ContentType => Headers["Content-Type"];
        public string? Host => Headers["Host"];

        /// <summary>The media type of the body without parameters, in lower case ("application/json").</summary>
        public string? MediaType => ContentType?.Split(';')[0].Trim().ToLowerInvariant();

        private static ILookup<string, string> ParseQuery(string query) =>
            query.Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(pair =>
                {
                    var equals = pair.IndexOf('=');
                    var name = equals < 0 ? pair : pair[..equals];
                    var value = equals < 0 ? string.Empty : pair[(equals + 1)..];
                    return (Name: Decode(name), Value: Decode(value));
                })
                .ToLookup(pair => pair.Name, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

        private static string Decode(string text) => Uri.UnescapeDataString(text.Replace('+', ' '));
    }

    /// <summary>The response is buffered and sent when the handler returns, with its exact Content-Length.</summary>
    public sealed class HttpServerResponse
    {
        private readonly ArrayBufferWriter<byte> body = new();

        public int StatusCode { get; set; } = 200;
        public HttpServerHeaders Headers { get; } = new();
        /// <summary>Trailing headers, sent after the body. Only HTTP/2 transports them (gRPC needs them); HTTP/1.1 drops them.</summary>
        public HttpServerHeaders Trailers { get; } = new();

        public ReadOnlyMemory<byte> Body => body.WrittenMemory;
        public IBufferWriter<byte> BodyWriter => body;

        public void Write(ReadOnlySpan<byte> bytes) => body.Write(bytes);

        public void Write(string text, string contentType)
        {
            Headers["Content-Type"] = contentType;
            Write(Encoding.UTF8.GetBytes(text));
        }

        /// <summary>Discards what was written: a handler that fails halfway can still answer with an error.</summary>
        public void Reset()
        {
            StatusCode = 200;
            Headers.Clear();
            Trailers.Clear();
            body.Clear();
        }
    }

    public sealed class HttpServerContext
    {
        public HttpServerContext(HttpServerRequest request, CancellationToken requestAborted)
        {
            Request = request;
            RequestAborted = requestAborted;
        }

        public HttpServerRequest Request { get; }
        public HttpServerResponse Response { get; } = new();
        /// <summary>Signalled when the client resets the request (HTTP/2), the handler times out or the server stops.</summary>
        public CancellationToken RequestAborted { get; }
        public IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
    }

    public interface IHttpHandler
    {
        public ValueTask HandleAsync(HttpServerContext context);
    }

    internal sealed class DelegateHttpHandler(Func<HttpServerContext, ValueTask> handle) : IHttpHandler
    {
        public ValueTask HandleAsync(HttpServerContext context) => handle(context);
    }

    internal static class HttpStatus
    {
        public static string ReasonPhrase(int status) => status switch
        {
            100 => "Continue",
            200 => "OK",
            201 => "Created",
            202 => "Accepted",
            204 => "No Content",
            301 => "Moved Permanently",
            302 => "Found",
            304 => "Not Modified",
            400 => "Bad Request",
            401 => "Unauthorized",
            403 => "Forbidden",
            404 => "Not Found",
            405 => "Method Not Allowed",
            406 => "Not Acceptable",
            408 => "Request Timeout",
            409 => "Conflict",
            411 => "Length Required",
            412 => "Precondition Failed",
            413 => "Content Too Large",
            414 => "URI Too Long",
            415 => "Unsupported Media Type",
            417 => "Expectation Failed",
            429 => "Too Many Requests",
            431 => "Request Header Fields Too Large",
            499 => "Client Closed Request",
            500 => "Internal Server Error",
            501 => "Not Implemented",
            503 => "Service Unavailable",
            504 => "Gateway Timeout",
            505 => "HTTP Version Not Supported",
            _ => "Unknown"
        };

        // No body allowed (RFC 9110 6.4.1).
        public static bool HasNoBody(int status) => status is < 200 or 204 or 304;
    }
}

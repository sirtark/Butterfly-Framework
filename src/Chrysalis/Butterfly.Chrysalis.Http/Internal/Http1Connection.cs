using Butterfly.Chrysalis.Http.Http2;
using Butterfly.Networking.Sockets;
using System.Globalization;
using System.Text;

namespace Butterfly.Chrysalis.Http.Internal
{
    /// <summary>HTTP/1.1 (and 1.0) on one connection: requests are served one after another (no pipelining overlap).</summary>
    internal sealed class Http1Connection(HttpServer server, TcpSocket socket, InputBuffer input, bool isSecure, string remoteAddress)
    {
        private const string Http2Preface = "PRI * HTTP/2.0";

        private readonly HttpServerOptions options = server.Options;

        public void Run()
        {
            var first = true;
            while (!server.IsStopping)
            {
                socket.ReceiveTimeout = first ? options.RequestTimeout : options.KeepAliveTimeout;
                bool keepAlive;
                try
                {
                    var line = input.ReadLine(options.MaxRequestLineLength, 414);
                    if (line is null)
                        return;
                    // Robustness (RFC 9112 2.2): empty lines before a request are ignored.
                    if (line.Length == 0)
                    {
                        line = input.ReadLine(options.MaxRequestLineLength, 414);
                        if (line is null)
                            return;
                    }

                    socket.ReceiveTimeout = options.RequestTimeout;
                    if (line == Http2Preface && options.EnableHttp2)
                    {
                        // Prior-knowledge h2c: the rest of the preface is "\r\nSM\r\n\r\n".
                        if (input.ReadLine(16, 400) != "" || input.ReadLine(16, 400) != "SM" || input.ReadLine(16, 400) != "")
                            return;
                        new Http2Connection(server, socket, input, isSecure, remoteAddress).Run(expectPreface: false);
                        return;
                    }

                    keepAlive = Serve(line);
                }
                catch (HttpProtocolException exception)
                {
                    WriteResponse(Error(exception.Status, exception.Message), "HTTP/1.1", isHead: false, keepAlive: false);
                    return;
                }

                if (!keepAlive)
                    return;
                first = false;
            }
        }

        // Serves one request; returns whether the connection stays open.
        private bool Serve(string requestLine)
        {
            var parts = requestLine.Split(' ');
            if (parts.Length != 3 || parts[0].Length == 0 || !parts[0].All(HttpServerHeaders.IsTokenChar))
                throw new HttpProtocolException(400, "Malformed request line.");

            var (method, target, version) = (parts[0], parts[1], parts[2]);
            if (version is not ("HTTP/1.1" or "HTTP/1.0"))
                throw new HttpProtocolException(version.StartsWith("HTTP/", StringComparison.Ordinal) ? 505 : 400, "Unsupported HTTP version.");
            target = NormalizeTarget(target);

            var headers = ReadHeaders(options.MaxRequestHeadersSize);
            var connection = headers["Connection"] ?? string.Empty;
            var keepAlive = version == "HTTP/1.1"
                ? !HasToken(connection, "close")
                : HasToken(connection, "keep-alive");

            if (version == "HTTP/1.1" && !headers.Contains("Host"))
                throw new HttpProtocolException(400, "The Host header is required.");

            var body = ReadBody(headers, version);
            var request = new HttpServerRequest(method, target, version, isSecure, remoteAddress, headers, body);
            var response = server.Dispatch(request);

            WriteResponse(response, version, method == "HEAD", keepAlive);
            return keepAlive;
        }

        private static string NormalizeTarget(string target)
        {
            if (target.StartsWith('/') || target == "*")
                return target;

            // Absolute form (sent to proxies): keep path and query.
            if (Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                return uri.PathAndQuery;
            throw new HttpProtocolException(400, "Malformed request target.");
        }

        private HttpServerHeaders ReadHeaders(int budget)
        {
            var headers = new HttpServerHeaders();
            while (true)
            {
                var line = input.ReadLine(budget, 431) ?? throw new IOException("The connection closed in the middle of the headers.");
                budget -= line.Length + 2;
                if (budget < 0)
                    throw new HttpProtocolException(431, "The request headers are too large.");
                if (line.Length == 0)
                    return headers;
                if (headers.Count >= options.MaxRequestHeaderCount)
                    throw new HttpProtocolException(431, "Too many request headers.");
                // Obsolete line folding is rejected (RFC 9112 5.2).
                if (line[0] is ' ' or '\t')
                    throw new HttpProtocolException(400, "Folded header lines are not accepted.");

                var colon = line.IndexOf(':');
                var name = colon > 0 ? line[..colon] : string.Empty;
                // No white space between the name and the colon (RFC 9112 5.1): a request smuggling vector.
                if (name.Length == 0 || !name.All(HttpServerHeaders.IsTokenChar))
                    throw new HttpProtocolException(400, "Malformed header line.");
                var value = line[(colon + 1)..].Trim(' ', '\t');
                if (value.AsSpan().IndexOfAny('\0', '\r') >= 0)
                    throw new HttpProtocolException(400, "Invalid character in a header value.");
                headers.AddUnchecked(name, value);
            }
        }

        private ReadOnlyMemory<byte> ReadBody(HttpServerHeaders headers, string version)
        {
            var transferEncoding = headers["Transfer-Encoding"];
            var contentLengths = headers.GetValues("Content-Length").SelectMany(value => value.Split(',')).Select(value => value.Trim()).Distinct().ToList();

            if (transferEncoding is not null)
            {
                // Both headers at once is how requests are smuggled past proxies (RFC 9112 6.3).
                if (contentLengths.Count > 0 || version == "HTTP/1.0")
                    throw new HttpProtocolException(400, "Transfer-Encoding is not allowed here.");
                if (!transferEncoding.Trim().Equals("chunked", StringComparison.OrdinalIgnoreCase))
                    throw new HttpProtocolException(501, "Only the chunked transfer coding is supported.");
                Continue(headers);
                return ReadChunked();
            }

            if (contentLengths.Count == 0)
                return ReadOnlyMemory<byte>.Empty;
            if (contentLengths.Count > 1 || !long.TryParse(contentLengths[0], NumberStyles.None, CultureInfo.InvariantCulture, out var length))
                throw new HttpProtocolException(400, "Invalid Content-Length.");
            if (length > options.MaxRequestBodySize)
                throw new HttpProtocolException(413, $"The request body is larger than {options.MaxRequestBodySize} bytes.");
            if (length == 0)
                return ReadOnlyMemory<byte>.Empty;

            Continue(headers);
            var body = new byte[length];
            input.ReadExactly(body);
            return body;
        }

        // Clients that sent "Expect: 100-continue" wait for this before sending the body.
        private void Continue(HttpServerHeaders headers)
        {
            var expect = headers["Expect"];
            if (expect is null)
                return;
            if (!expect.Equals("100-continue", StringComparison.OrdinalIgnoreCase))
                throw new HttpProtocolException(417, "Unsupported expectation.");
            Write(Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n"));
        }

        private byte[] ReadChunked()
        {
            using var body = new MemoryStream();
            while (true)
            {
                var line = input.ReadLine(1024, 400) ?? throw new IOException("The connection closed in the middle of the body.");
                var sizeText = line.Split(';')[0].Trim();
                if (sizeText.Length == 0 || !long.TryParse(sizeText, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var size) || size < 0)
                    throw new HttpProtocolException(400, "Malformed chunk size.");
                if (size == 0)
                    break;
                if (body.Length + size > options.MaxRequestBodySize)
                    throw new HttpProtocolException(413, $"The request body is larger than {options.MaxRequestBodySize} bytes.");

                var chunk = new byte[size];
                input.ReadExactly(chunk);
                body.Write(chunk);
                if (input.ReadLine(2, 400) != "")
                    throw new HttpProtocolException(400, "Malformed chunk.");
            }

            // Trailer fields are read and discarded.
            ReadHeaders(options.MaxRequestHeadersSize);
            return body.ToArray();
        }

        private void WriteResponse(HttpServerResponse response, string version, bool isHead, bool keepAlive)
        {
            var head = new StringBuilder(256);
            head.Append("HTTP/1.1 ").Append(response.StatusCode.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(HttpStatus.ReasonPhrase(response.StatusCode)).Append("\r\n");
            head.Append("Date: ").Append(DateTimeOffset.UtcNow.ToString("R", CultureInfo.InvariantCulture)).Append("\r\n");
            if (options.ServerHeader is not null && !response.Headers.Contains("Server"))
                head.Append("Server: ").Append(options.ServerHeader).Append("\r\n");

            foreach (var (name, value) in response.Headers)
            {
                // Framing headers belong to the server.
                if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) || name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
                    continue;
                head.Append(name).Append(": ").Append(value).Append("\r\n");
            }

            var hasBody = !HttpStatus.HasNoBody(response.StatusCode);
            if (hasBody)
                head.Append("Content-Length: ").Append(response.Body.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            if (!keepAlive)
                head.Append("Connection: close\r\n");
            else if (version == "HTTP/1.0")
                head.Append("Connection: keep-alive\r\n");
            head.Append("\r\n");

            var headBytes = Encoding.Latin1.GetBytes(head.ToString());
            var body = hasBody && !isHead ? response.Body.Span : ReadOnlySpan<byte>.Empty;
            var message = new byte[headBytes.Length + body.Length];
            headBytes.CopyTo(message, 0);
            body.CopyTo(message.AsSpan(headBytes.Length));
            Write(message);
        }

        private void Write(ReadOnlySpan<byte> bytes)
        {
            input.Stream.Write(bytes);
            input.Stream.Flush();
        }

        private static HttpServerResponse Error(int status, string message)
        {
            var response = new HttpServerResponse { StatusCode = status };
            response.Write(message, "text/plain; charset=utf-8");
            return response;
        }

        private static bool HasToken(string list, string token) =>
            list.Split(',').Any(item => item.Trim().Equals(token, StringComparison.OrdinalIgnoreCase));
    }
}

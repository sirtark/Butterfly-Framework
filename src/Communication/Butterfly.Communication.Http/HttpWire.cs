using System.Globalization;
using System.Text;

namespace Butterfly.Communication.Http
{
    /// <summary>
    /// HTTP/1.1 message framing (RFC 9112): writing request heads and reading response heads. Public so other
    /// protocols that start with an HTTP exchange (WebSockets) can reuse it.
    /// </summary>
    public static class HttpWire
    {
        private const int MaxHeaderBytes = 256 * 1024;
        private const int MaxHeaderCount = 500;

        /// <summary>Writes the request line and headers (adding Host when missing) followed by the blank line.</summary>
        public static void WriteRequestHead(Stream stream, string method, Uri uri, HttpHeaders headers)
        {
            var builder = new StringBuilder(256);
            builder.Append(method).Append(' ').Append(RequestTarget(method, uri)).Append(" HTTP/1.1\r\n");

            if (!headers.Contains("Host"))
                builder.Append("Host: ").Append(HostHeader(uri)).Append("\r\n");

            foreach (var (name, value) in headers)
                builder.Append(name).Append(": ").Append(value).Append("\r\n");

            builder.Append("\r\n");

            // Header values are Latin-1 on the wire; Encoding.Latin1 maps anything else to '?'.
            stream.Write(Encoding.Latin1.GetBytes(builder.ToString()));
        }

        /// <summary>Reads a response head. 1xx interim responses are skipped, except 101 Switching Protocols.</summary>
        /// <exception cref="HttpException">The server did not answer with a valid HTTP/1.x response.</exception>
        public static HttpResponseHead ReadResponseHead(ProtocolReader reader)
        {
            while (true)
            {
                HttpResponseHead head = ReadOneHead(reader);
                if (head.StatusCode is >= 100 and < 200 && head.StatusCode != 101)
                    continue;
                return head;
            }
        }

        private static HttpResponseHead ReadOneHead(ProtocolReader reader)
        {
            string statusLine = reader.ReadLine(Encoding.Latin1)
                ?? throw new EndOfStreamException("The server closed the connection without answering.");

            // HTTP/1.1 200 OK   (the reason phrase is optional)
            if (!statusLine.StartsWith("HTTP/1.", StringComparison.Ordinal) || statusLine.Length < 12 || statusLine[8] != ' '
                || !int.TryParse(statusLine.AsSpan(9, 3), NumberStyles.None, CultureInfo.InvariantCulture, out int status)
                || (statusLine.Length > 12 && statusLine[12] != ' '))
            {
                throw new HttpException($"Invalid HTTP status line: '{Truncate(statusLine)}'.");
            }

            var version = statusLine[7] == '0' ? new Version(1, 0) : new Version(1, 1);
            string reason = statusLine.Length > 13 ? statusLine[13..] : "";

            var headers = new HttpHeaders();
            int total = 0;
            while (true)
            {
                string line = reader.ReadLine(Encoding.Latin1)
                    ?? throw new EndOfStreamException("The connection closed in the middle of the response headers.");

                if (line.Length == 0)
                    break;

                total += line.Length;
                if (total > MaxHeaderBytes || headers.Count > MaxHeaderCount)
                    throw new HttpException("The response headers are too large.");

                // obs-fold: a line starting with whitespace continues the previous header.
                if (line[0] is ' ' or '\t')
                {
                    headers.AppendToLast(line);
                    continue;
                }

                int colon = line.IndexOf(':');
                if (colon <= 0)
                    throw new HttpException($"Invalid response header line: '{Truncate(line)}'.");

                string name = line[..colon];
                if (!HttpHeaders.IsToken(name))
                    throw new HttpException($"Invalid response header name: '{Truncate(name)}'.");

                headers.AddUnvalidated(name, line[(colon + 1)..].Trim(' ', '\t'));
            }

            return new HttpResponseHead(version, status, reason, headers);
        }

        /// <summary>The Host header value: punycode host, brackets for IPv6, port only when not the scheme default.</summary>
        public static string HostHeader(Uri uri)
        {
            string host = uri.HostNameType == UriHostNameType.IPv6 ? $"[{StripZone(uri.DnsSafeHost)}]" : uri.IdnHost;
            return uri.IsDefaultPort ? host : $"{host}:{uri.Port.ToString(CultureInfo.InvariantCulture)}";
        }

        /// <summary>The host to connect to (no brackets, punycode).</summary>
        public static string ConnectHost(Uri uri) => uri.HostNameType == UriHostNameType.IPv6 ? uri.DnsSafeHost : uri.IdnHost;

        private static string RequestTarget(string method, Uri uri)
            => method == HttpMethod.Options && uri.AbsolutePath == "*" ? "*" : uri.PathAndQuery;

        private static string StripZone(string host) => host.Split('%')[0];

        private static string Truncate(string text) => text.Length <= 100 ? text : text[..100] + "...";
    }
}

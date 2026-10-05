using System.Text;

namespace Butterfly.Communication.Mail
{
    /// <summary>A parsed MIME entity: a whole message, a multipart container or a leaf part.</summary>
    public sealed class MimeEntity
    {
        private const int MaxDepth = 32;

        private MimeEntity(MimeHeaders headers, IReadOnlyList<MimeEntity> children, byte[] content)
        {
            Headers = headers;
            Children = children;
            Content = content;
            ContentType = headers.Get("Content-Type") is { } type ? ContentType.Parse(type) : ContentType.Default;
            ContentDisposition = headers.Get("Content-Disposition") is { } disposition ? ContentDisposition.Parse(disposition) : null;
        }

        public MimeHeaders Headers { get; }
        public ContentType ContentType { get; }
        public ContentDisposition? ContentDisposition { get; }

        /// <summary>The parts of a multipart entity; empty for leaves.</summary>
        public IReadOnlyList<MimeEntity> Children { get; }

        /// <summary>The body of a leaf with its transfer encoding (base64, quoted-printable) already undone.</summary>
        public byte[] Content { get; }

        public bool IsMultipart => ContentType.IsMultipart;

        public string? ContentId => Headers.Get("Content-ID")?.Trim().Trim('<', '>');

        /// <summary>The file name from Content-Disposition, or from the older Content-Type "name" parameter.</summary>
        public string? FileName
        {
            get
            {
                string? name = ContentDisposition?.FileName ?? ContentType.Name;
                return name is null ? null : MimeEncoding.DecodeHeaderText(name);
            }
        }

        /// <summary>Marked "attachment", or a named part without any disposition.</summary>
        public bool IsAttachment
            => !IsMultipart && (ContentDisposition?.IsAttachment == true || (ContentDisposition is null && FileName is not null));

        /// <summary>The body decoded with its charset.</summary>
        public string GetText() => Charsets.GetEncoding(ContentType.Charset).GetString(Content);

        /// <summary>This entity and all nested parts, depth first.</summary>
        public IEnumerable<MimeEntity> Descendants()
        {
            yield return this;
            foreach (MimeEntity child in Children)
            {
                foreach (MimeEntity entity in child.Descendants())
                    yield return entity;
            }
        }

        /// <summary>Parses a message (or a part). Lenient: accepts bare LF line endings and malformed parts.</summary>
        public static MimeEntity Parse(ReadOnlySpan<byte> data) => Parse(data, 0);

        private static MimeEntity Parse(ReadOnlySpan<byte> data, int depth)
        {
            var headers = new MimeHeaders();
            int bodyStart = ParseHeaders(data, headers);
            ReadOnlySpan<byte> body = data[bodyStart..];

            string? type = headers.Get("Content-Type");
            ContentType contentType = type is null ? ContentType.Default : ContentType.Parse(type);

            if (contentType.IsMultipart && contentType.Boundary is { Length: > 0 } boundary && depth < MaxDepth)
            {
                var children = SplitMultipart(body, boundary).Select(part => Parse(part, depth + 1)).ToList();
                return new MimeEntity(headers, children, []);
            }

            string encoding = headers.Get("Content-Transfer-Encoding")?.Trim().ToLowerInvariant() ?? "7bit";
            byte[] content = encoding switch
            {
                "base64" => DecodeBase64OrRaw(body),
                "quoted-printable" => MimeEncoding.DecodeQuotedPrintable(Encoding.Latin1.GetString(body)),
                _ => body.ToArray()
            };

            return new MimeEntity(headers, [], content);
        }

        private static byte[] DecodeBase64OrRaw(ReadOnlySpan<byte> raw)
        {
            try
            {
                return MimeEncoding.DecodeBase64(Encoding.ASCII.GetString(raw));
            }
            catch (FormatException)
            {
                return raw.ToArray();
            }
        }

        /// <summary>Reads the header block (unfolding continuation lines) and returns where the body starts.</summary>
        private static int ParseHeaders(ReadOnlySpan<byte> data, MimeHeaders headers)
        {
            int position = 0;
            string? name = null;
            var value = new StringBuilder();

            while (position < data.Length)
            {
                int lineStart = position;
                int end = data[position..].IndexOf((byte)'\n');
                int next = end < 0 ? data.Length : position + end + 1;
                ReadOnlySpan<byte> line = data[position..(end < 0 ? data.Length : position + end)];
                if (!line.IsEmpty && line[^1] == '\r')
                    line = line[..^1];

                position = next;

                if (line.IsEmpty)
                    break;

                // Header bytes are usually ASCII; raw UTF-8 (RFC 6532) is decoded as such, anything else as Latin-1.
                string text = Utf8OrLatin1(line);

                if (text[0] is ' ' or '\t')
                {
                    if (name is not null)
                        value.Append(' ').Append(text.Trim());
                    continue;
                }

                if (name is not null)
                    headers.AddRaw(name, value.ToString());

                int colon = text.IndexOf(':');
                if (colon <= 0)
                {
                    // Not a header: treat the rest as body (broken messages without the blank line).
                    return lineStart;
                }

                name = text[..colon].Trim();
                value.Clear().Append(text[(colon + 1)..].Trim());
            }

            if (name is not null)
                headers.AddRaw(name, value.ToString());

            return Math.Min(position, data.Length);
        }

        private static List<byte[]> SplitMultipart(ReadOnlySpan<byte> body, string boundary)
        {
            byte[] delimiter = Encoding.ASCII.GetBytes("--" + boundary);
            var parts = new List<byte[]>();
            int partStart = -1;
            int position = 0;

            while (position <= body.Length)
            {
                int end = body[position..].IndexOf((byte)'\n');
                int lineEnd = end < 0 ? body.Length : position + end;
                ReadOnlySpan<byte> line = body[position..lineEnd].TrimEnd("\r \t"u8);

                if (line.StartsWith(delimiter))
                {
                    ReadOnlySpan<byte> rest = line[delimiter.Length..];
                    bool closing = rest.SequenceEqual("--"u8);

                    if (rest.IsEmpty || closing)
                    {
                        if (partStart >= 0)
                        {
                            // The CRLF before the delimiter belongs to the delimiter, not to the part.
                            int partEnd = position;
                            if (partEnd > partStart && body[partEnd - 1] == '\n') partEnd--;
                            if (partEnd > partStart && body[partEnd - 1] == '\r') partEnd--;
                            parts.Add(body[partStart..partEnd].ToArray());
                        }

                        if (closing)
                            return parts;

                        partStart = Math.Min(lineEnd + 1, body.Length);
                    }
                }

                if (end < 0)
                    break;
                position = lineEnd + 1;
            }

            // No closing delimiter (truncated message): keep the last part.
            if (partStart >= 0 && partStart < body.Length)
                parts.Add(body[partStart..].ToArray());

            return parts;
        }

        private static string Utf8OrLatin1(ReadOnlySpan<byte> bytes)
        {
            if (Ascii.IsValid(bytes))
                return Encoding.ASCII.GetString(bytes);

            return System.Text.Unicode.Utf8.IsValid(bytes) ? Encoding.UTF8.GetString(bytes) : Encoding.Latin1.GetString(bytes);
        }
    }
}

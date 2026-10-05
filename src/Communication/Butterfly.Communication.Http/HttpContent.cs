using System.Security.Cryptography;
using System.Text;

namespace Butterfly.Communication.Http
{
    /// <summary>A request body. Use the static factories, or <see cref="MultipartFormContent"/> for file uploads.</summary>
    public abstract class HttpContent
    {
        public string? ContentType { get; protected init; }

        /// <summary>The body length, or null to send it with chunked transfer encoding.</summary>
        public abstract long? Length { get; }

        /// <summary>Whether the body can be sent again (after a redirect or a stale pooled connection).</summary>
        public virtual bool IsReplayable => true;

        public abstract void WriteTo(Stream destination);

        public static HttpContent FromString(string text, string mediaType = "text/plain", Encoding? encoding = null)
        {
            encoding ??= Encoding.UTF8;
            return new BytesContent(encoding.GetBytes(text), $"{mediaType}; charset={encoding.WebName}");
        }

        public static HttpContent FromJson(string json) => FromString(json, "application/json");

        public static HttpContent FromBytes(byte[] bytes, string mediaType = "application/octet-stream") => new BytesContent(bytes, mediaType);

        /// <summary>A body read from <paramref name="stream"/>; replayable only if the stream can seek.</summary>
        public static HttpContent FromStream(Stream stream, string mediaType = "application/octet-stream", long? length = null)
            => new StreamContent(stream, mediaType, length ?? (stream.CanSeek ? stream.Length - stream.Position : null));

        /// <summary>application/x-www-form-urlencoded fields.</summary>
        public static HttpContent FromForm(IEnumerable<KeyValuePair<string, string>> fields)
            => new BytesContent(Encoding.ASCII.GetBytes(EncodeForm(fields)), "application/x-www-form-urlencoded");

        public static HttpContent FromForm(params (string Name, string Value)[] fields)
            => FromForm(fields.Select(f => new KeyValuePair<string, string>(f.Name, f.Value)));

        internal static string EncodeForm(IEnumerable<KeyValuePair<string, string>> fields)
            => string.Join('&', fields.Select(f => $"{FormEscape(f.Key)}={FormEscape(f.Value)}"));

        private static string FormEscape(string value) => Uri.EscapeDataString(value).Replace("%20", "+");

        private sealed class BytesContent : HttpContent
        {
            private readonly byte[] _bytes;

            public BytesContent(byte[] bytes, string contentType)
            {
                _bytes = bytes;
                ContentType = contentType;
            }

            public override long? Length => _bytes.Length;

            public override void WriteTo(Stream destination) => destination.Write(_bytes);
        }

        private sealed class StreamContent : HttpContent
        {
            private readonly Stream _stream;
            private readonly long _start;

            public StreamContent(Stream stream, string contentType, long? length)
            {
                _stream = stream;
                _start = stream.CanSeek ? stream.Position : 0;
                Length = length;
                ContentType = contentType;
            }

            public override long? Length { get; }

            public override bool IsReplayable => _stream.CanSeek;

            public override void WriteTo(Stream destination)
            {
                if (_stream.CanSeek)
                    _stream.Position = _start;
                _stream.CopyTo(destination);
            }
        }
    }

    /// <summary>multipart/form-data: form fields and files, as an HTML form with enctype="multipart/form-data" sends them.</summary>
    public sealed class MultipartFormContent : HttpContent
    {
        private readonly List<(byte[] Head, byte[]? Bytes, Stream? Stream)> _parts = [];
        private readonly string _boundary = "----butterfly-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));

        public MultipartFormContent() => ContentType = $"multipart/form-data; boundary={_boundary}";

        public MultipartFormContent Add(string name, string value)
        {
            _parts.Add((Head($"Content-Disposition: form-data; name=\"{Quote(name)}\"\r\n"), Encoding.UTF8.GetBytes(value), null));
            return this;
        }

        public MultipartFormContent AddFile(string name, string fileName, byte[] content, string contentType = "application/octet-stream")
        {
            _parts.Add((FileHead(name, fileName, contentType), content, null));
            return this;
        }

        /// <summary>Adds a file read from a stream when the body is sent; the stream must stay open until then.</summary>
        public MultipartFormContent AddFile(string name, string fileName, Stream content, string contentType = "application/octet-stream")
        {
            _parts.Add((FileHead(name, fileName, contentType), null, content));
            return this;
        }

        public override long? Length
        {
            get
            {
                long total = Encoding.ASCII.GetByteCount($"--{_boundary}--\r\n");
                foreach (var (head, bytes, stream) in _parts)
                {
                    long? size = bytes is not null ? bytes.Length : stream!.CanSeek ? stream.Length - stream.Position : null;
                    if (size is null)
                        return null;
                    total += head.Length + size.Value + 2;
                }
                return total;
            }
        }

        public override bool IsReplayable => _parts.All(p => p.Stream is null);

        public override void WriteTo(Stream destination)
        {
            foreach (var (head, bytes, stream) in _parts)
            {
                destination.Write(head);
                if (bytes is not null)
                    destination.Write(bytes);
                else
                    stream!.CopyTo(destination);
                destination.Write("\r\n"u8);
            }

            destination.Write(Encoding.ASCII.GetBytes($"--{_boundary}--\r\n"));
        }

        private byte[] FileHead(string name, string fileName, string contentType)
            => Head($"Content-Disposition: form-data; name=\"{Quote(name)}\"; filename=\"{Quote(fileName)}\"\r\nContent-Type: {contentType}\r\n");

        private byte[] Head(string headers) => Encoding.UTF8.GetBytes($"--{_boundary}\r\n{headers}\r\n");

        // Per the HTML standard, quotes and line breaks in names are percent-encoded.
        private static string Quote(string value) => value.Replace("\"", "%22").Replace("\r", "%0D").Replace("\n", "%0A");
    }
}

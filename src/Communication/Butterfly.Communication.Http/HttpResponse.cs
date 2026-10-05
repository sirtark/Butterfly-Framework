using System.Text;

namespace Butterfly.Communication.Http
{
    public sealed class HttpResponse : IDisposable
    {
        internal HttpResponse(HttpRequest request, HttpResponseHead head, Stream body)
        {
            Request = request;
            StatusCode = head.StatusCode;
            ReasonPhrase = head.ReasonPhrase;
            Version = head.Version;
            Headers = head.Headers;
            Body = body;
        }

        /// <summary>The request that produced this response (the last one when redirects were followed).</summary>
        public HttpRequest Request { get; }

        public Uri RequestUri => Request.Uri;
        public int StatusCode { get; }
        public string ReasonPhrase { get; }
        public Version Version { get; }
        public HttpHeaders Headers { get; }

        /// <summary>
        /// The decoded body (already decompressed). With <see cref="HttpCompletion.Buffered"/> it is in memory;
        /// with <see cref="HttpCompletion.Streamed"/> it reads from the network and must be disposed.
        /// </summary>
        public Stream Body { get; }

        public bool IsSuccessStatusCode => StatusCode is >= 200 and <= 299;

        public string? ContentType => Headers.ContentType;

        /// <exception cref="HttpStatusException">The status code is not 2xx.</exception>
        public HttpResponse EnsureSuccessStatusCode()
        {
            if (!IsSuccessStatusCode)
                throw new HttpStatusException(this);
            return this;
        }

        public byte[] ReadAsBytes()
        {
            if (Body is MemoryStream memory && memory.Position == 0)
                return memory.ToArray();

            using var copy = new MemoryStream();
            Body.CopyTo(copy);
            return copy.ToArray();
        }

        /// <summary>The body as text, decoded with the Content-Type charset (UTF-8 when none is given).</summary>
        public string ReadAsString()
        {
            byte[] bytes = ReadAsBytes();
            Encoding encoding = Charsets.GetEncoding(MediaTypeParameter(ContentType, "charset"));

            // A BOM wins over the declared charset, as browsers do.
            if (bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble))
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);

            return encoding.GetString(bytes);
        }

        public Task<byte[]> ReadAsBytesAsync(CancellationToken cancellationToken = default) => Task.Run(ReadAsBytes, cancellationToken);

        public Task<string> ReadAsStringAsync(CancellationToken cancellationToken = default) => Task.Run(ReadAsString, cancellationToken);

        public void Dispose() => Body.Dispose();

        public override string ToString() => $"HTTP/{Version} {StatusCode} {ReasonPhrase}";

        internal static string? MediaTypeParameter(string? contentType, string parameter)
        {
            if (contentType is null)
                return null;

            foreach (string part in contentType.Split(';').Skip(1))
            {
                int equals = part.IndexOf('=');
                if (equals > 0 && part[..equals].Trim().Equals(parameter, StringComparison.OrdinalIgnoreCase))
                    return part[(equals + 1)..].Trim().Trim('"');
            }

            return null;
        }
    }

    public sealed record HttpResponseHead(Version Version, int StatusCode, string ReasonPhrase, HttpHeaders Headers);

    /// <summary>When <see cref="HttpClient.Send"/> returns.</summary>
    public enum HttpCompletion
    {
        /// <summary>After the whole body has been read into memory; the connection is already free.</summary>
        Buffered,

        /// <summary>As soon as the headers arrive; the body is read from the network while the caller consumes it.</summary>
        Streamed
    }

    public class HttpException : CommunicationException
    {
        public HttpException(string message, Exception? innerException = null) : base(message, innerException) { }
    }

    public sealed class HttpStatusException : HttpException
    {
        internal HttpStatusException(HttpResponse response)
            : base($"{response.Request.Method} {response.RequestUri} returned {response.StatusCode} {response.ReasonPhrase}.")
        {
            StatusCode = response.StatusCode;
            ReasonPhrase = response.ReasonPhrase;
            Response = response;
        }

        public int StatusCode { get; }
        public string ReasonPhrase { get; }

        /// <summary>The failed response, whose body often explains the error.</summary>
        public HttpResponse Response { get; }
    }
}

using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Security;
using System.Reflection;

namespace Butterfly.Communication.Http
{
    public sealed class HttpClientOptions
    {
        public ConnectionOptions Connection { get; init; } = ConnectionOptions.Default;

        /// <summary>Limit for a whole request (connecting, sending and, in buffered mode, reading the body).</summary>
        public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(100);

        /// <summary>Redirects followed automatically; 0 returns 3xx responses to the caller.</summary>
        public int MaxRedirects { get; init; } = 10;

        /// <summary>Sends Accept-Encoding and transparently decodes gzip, deflate and brotli bodies.</summary>
        public bool AutomaticDecompression { get; init; } = true;

        public string UserAgent { get; init; } = DefaultUserAgent;

        /// <summary>Headers added to every request (a request header with the same name wins).</summary>
        public HttpHeaders DefaultHeaders { get; init; } = new();

        /// <summary>Stores and sends cookies; null (the default) disables cookie handling.</summary>
        public CookieContainer? Cookies { get; init; }

        /// <summary>How long an idle keep-alive connection is kept for reuse.</summary>
        public TimeSpan PooledConnectionIdleTimeout { get; init; } = TimeSpan.FromSeconds(60);

        /// <summary>Largest body read in <see cref="HttpCompletion.Buffered"/> mode.</summary>
        public long MaxBufferedContentLength { get; init; } = 256L * 1024 * 1024;

        internal static string DefaultUserAgent { get; } =
            "Butterfly/" + (typeof(HttpClient).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0");
    }

    /// <summary>
    /// HTTP/1.1 client. Thread-safe: concurrent requests use separate pooled connections. Dispose it to close them.
    /// </summary>
    public sealed class HttpClient : IDisposable
    {
        private static readonly SslApplicationProtocol[] s_alpn = [SslApplicationProtocol.Http11];

        private readonly ConcurrentDictionary<string, ConcurrentStack<PooledConnection>> _pool = new();
        private volatile bool _disposed;

        public HttpClient(HttpClientOptions? options = null) => Options = options ?? new HttpClientOptions();

        public HttpClientOptions Options { get; }

        public HttpResponse Send(HttpRequest request, HttpCompletion completion = HttpCompletion.Buffered, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ObjectDisposedException.ThrowIf(_disposed, this);

            using var timeout = new CancellationTokenSource(Options.Timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            var context = new RequestContext();

            using (linked.Token.Register(context.Abort))
            {
                try
                {
                    HttpResponse response = SendFollowingRedirects(request, completion, context, linked.Token);
                    linked.Token.ThrowIfCancellationRequested();
                    return response;
                }
                catch (Exception ex) when (linked.IsCancellationRequested && ex is not OperationCanceledException)
                {
                    throw Cancelled(cancellationToken, timeout, ex);
                }
                catch (OperationCanceledException ex) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    throw Cancelled(cancellationToken, timeout, ex);
                }
            }
        }

        public Task<HttpResponse> SendAsync(HttpRequest request, HttpCompletion completion = HttpCompletion.Buffered, CancellationToken cancellationToken = default)
            => Task.Run(() => Send(request, completion, cancellationToken), cancellationToken);

        public HttpResponse Get(string uri) => Send(HttpRequest.Get(uri));
        public HttpResponse Post(string uri, HttpContent content) => Send(HttpRequest.Post(uri, content));
        public HttpResponse Put(string uri, HttpContent content) => Send(HttpRequest.Put(uri, content));
        public HttpResponse Patch(string uri, HttpContent content) => Send(HttpRequest.Patch(uri, content));
        public HttpResponse Delete(string uri) => Send(HttpRequest.Delete(uri));

        /// <exception cref="HttpStatusException">The status code is not 2xx.</exception>
        public string GetString(string uri, CancellationToken cancellationToken = default)
        {
            using HttpResponse response = Send(HttpRequest.Get(uri), HttpCompletion.Buffered, cancellationToken).EnsureSuccessStatusCode();
            return response.ReadAsString();
        }

        /// <exception cref="HttpStatusException">The status code is not 2xx.</exception>
        public byte[] GetBytes(string uri, CancellationToken cancellationToken = default)
        {
            using HttpResponse response = Send(HttpRequest.Get(uri), HttpCompletion.Buffered, cancellationToken).EnsureSuccessStatusCode();
            return response.ReadAsBytes();
        }

        /// <summary>Streams the body; dispose the returned stream to release the connection.</summary>
        public Stream GetStream(string uri, CancellationToken cancellationToken = default)
            => Send(HttpRequest.Get(uri), HttpCompletion.Streamed, cancellationToken).EnsureSuccessStatusCode().Body;

        public Task<string> GetStringAsync(string uri, CancellationToken cancellationToken = default)
            => Task.Run(() => GetString(uri, cancellationToken), cancellationToken);

        public Task<byte[]> GetBytesAsync(string uri, CancellationToken cancellationToken = default)
            => Task.Run(() => GetBytes(uri, cancellationToken), cancellationToken);

        public void Dispose()
        {
            _disposed = true;
            foreach (var stack in _pool.Values)
            {
                while (stack.TryPop(out var pooled))
                    pooled.Connection.Dispose();
            }
        }

        private HttpResponse SendFollowingRedirects(HttpRequest request, HttpCompletion completion, RequestContext context, CancellationToken cancellationToken)
        {
            for (int redirects = 0; ; redirects++)
            {
                HttpResponse response = SendOnce(request, completion, context, cancellationToken);

                if (Options.MaxRedirects == 0 || response.StatusCode is not (301 or 302 or 303 or 307 or 308)
                    || response.Headers.Get("Location") is not { } location
                    || !Uri.TryCreate(request.Uri, location, out Uri? target)
                    || (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps)
                    // Never downgrade from HTTPS to HTTP silently.
                    || (request.Uri.Scheme == Uri.UriSchemeHttps && target.Scheme == Uri.UriSchemeHttp))
                {
                    return response;
                }

                if (redirects >= Options.MaxRedirects)
                {
                    response.Dispose();
                    throw new HttpException($"Too many redirects (more than {Options.MaxRedirects}).");
                }

                response.Dispose();
                request = Redirect(request, response.StatusCode, target);
            }
        }

        private static HttpRequest Redirect(HttpRequest request, int status, Uri target)
        {
            // 303 always becomes GET; 301/302 turn POST into GET (what every browser does); 307/308 keep everything.
            bool toGet = (status == 303 && request.Method != HttpMethod.Head) || (status is 301 or 302 && request.Method == HttpMethod.Post);

            if (!toGet && request.Content is { IsReplayable: false })
                throw new HttpException($"Cannot follow the {status} redirect: the request body cannot be sent twice.");

            var next = new HttpRequest(toGet ? HttpMethod.Get : request.Method, target) { Content = toGet ? null : request.Content };
            bool sameOrigin = Uri.Compare(request.Uri, target, UriComponents.SchemeAndServer, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) == 0;

            foreach (var (name, value) in request.Headers)
            {
                // Credentials and cookies never leak to another origin; the body headers go with the body.
                if (!sameOrigin && (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) || name.Equals("Cookie", StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (toGet && name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
                    continue;
                next.Headers.Add(name, value);
            }

            return next;
        }

        private HttpResponse SendOnce(HttpRequest request, HttpCompletion completion, RequestContext context, CancellationToken cancellationToken)
        {
            Uri uri = request.Uri;
            bool https = uri.Scheme == Uri.UriSchemeHttps;
            string key = $"{uri.Scheme}://{uri.IdnHost}:{uri.Port}";
            HttpHeaders headers = BuildHeaders(request);

            // A pooled connection may have been closed by the server in the meantime: retry once on a fresh one.
            for (int attempt = 0; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                NetworkConnection? connection = attempt == 0 ? Rent(key) : null;
                bool reused = connection is not null;
                connection ??= NetworkConnection.Connect(HttpWire.ConnectHost(uri), uri.Port, Options.Connection, https, s_alpn, cancellationToken);
                context.Connection = connection;

                HttpResponseHead head;
                try
                {
                    WriteRequest(connection, request, headers);
                    head = HttpWire.ReadResponseHead(connection.Reader);
                }
                catch (Exception ex) when (reused && ex is IOException && request.Content is null or { IsReplayable: true } && !cancellationToken.IsCancellationRequested)
                {
                    connection.Dispose();
                    continue;
                }
                catch
                {
                    connection.Dispose();
                    throw;
                }

                StoreCookies(uri, head.Headers);
                return CreateResponse(request, head, connection, key, completion);
            }
        }

        private HttpHeaders BuildHeaders(HttpRequest request)
        {
            var headers = new HttpHeaders();
            foreach (var (name, value) in Options.DefaultHeaders)
            {
                if (!request.Headers.Contains(name))
                    headers.Add(name, value);
            }
            headers.AddRange(request.Headers);

            if (!headers.Contains("User-Agent") && Options.UserAgent.Length > 0)
                headers.Set("User-Agent", Options.UserAgent);

            if (Options.AutomaticDecompression && !headers.Contains("Accept-Encoding"))
                headers.Set("Accept-Encoding", "gzip, deflate, br");

            if (Options.Cookies?.GetCookieHeader(request.Uri) is { Length: > 0 } cookies && !headers.Contains("Cookie"))
                headers.Set("Cookie", cookies);

            if (request.Content is { } content)
            {
                if (content.ContentType is { } type && !headers.Contains("Content-Type"))
                    headers.Set("Content-Type", type);

                headers.Remove("Content-Length");
                headers.Remove("Transfer-Encoding");
                if (content.Length is { } length)
                    headers.Set("Content-Length", length.ToString(System.Globalization.CultureInfo.InvariantCulture));
                else
                    headers.Set("Transfer-Encoding", "chunked");
            }
            else if (request.Method is HttpMethod.Post or HttpMethod.Put or HttpMethod.Patch)
            {
                // RFC 9110: a request that usually has a body says explicitly that this one is empty.
                headers.Set("Content-Length", "0");
            }

            return headers;
        }

        private static void WriteRequest(NetworkConnection connection, HttpRequest request, HttpHeaders headers)
        {
            // Headers and body go through one buffer so small requests leave in a single packet.
            // Not disposed: disposing a BufferedStream closes the connection underneath.
            var buffered = new BufferedStream(connection.Stream, 16 * 1024);
            HttpWire.WriteRequestHead(buffered, request.Method, request.Uri, headers);

            if (request.Content is { } content)
            {
                if (content.Length is null)
                {
                    var chunked = new ChunkedWriteStream(buffered);
                    content.WriteTo(chunked);
                    chunked.Complete();
                }
                else
                {
                    content.WriteTo(buffered);
                }
            }

            buffered.Flush();
        }

        private HttpResponse CreateResponse(HttpRequest request, HttpResponseHead head, NetworkConnection connection, string key, HttpCompletion completion)
        {
            ReadOnlyBodyStream framing = Framing(request, head, connection.Reader);

            bool keepAlive = framing is not UntilCloseStream
                && !head.Headers.HasToken("Connection", "close")
                && !request.Headers.HasToken("Connection", "close")
                && (head.Version.Minor >= 1 || head.Headers.HasToken("Connection", "keep-alive"));

            void Release(bool complete)
            {
                if (complete && keepAlive && !_disposed)
                    Return(key, connection);
                else
                    connection.Dispose();
            }

            Stream decoded = Decode(framing, head.Headers);
            var body = new ResponseBodyStream(decoded, framing, Release);

            if (completion == HttpCompletion.Streamed || head.StatusCode == 101)
                return new HttpResponse(request, head, body);

            try
            {
                var memory = new MemoryStream();
                byte[] chunk = new byte[81920];
                for (int read; (read = body.Read(chunk)) > 0;)
                {
                    if (memory.Length + read > Options.MaxBufferedContentLength)
                        throw new HttpException($"The response body is larger than {Options.MaxBufferedContentLength} bytes; use HttpCompletion.Streamed.");
                    memory.Write(chunk, 0, read);
                }

                memory.Position = 0;
                return new HttpResponse(request, head, memory);
            }
            catch
            {
                body.Dispose();
                throw;
            }
        }

        private static ReadOnlyBodyStream Framing(HttpRequest request, HttpResponseHead head, ProtocolReader reader)
        {
            if (request.Method == HttpMethod.Head || head.StatusCode is 204 or 304 or 101 or < 200)
                return new EmptyBodyStream();

            // Transfer-Encoding overrides Content-Length (RFC 9112 6.3); chunked must be the final coding.
            if (head.Headers.Contains("Transfer-Encoding"))
            {
                string last = head.Headers.GetValues("Transfer-Encoding").SelectMany(v => v.Split(',')).Last().Trim();
                return last.Equals("chunked", StringComparison.OrdinalIgnoreCase) ? new ChunkedStream(reader) : new UntilCloseStream(reader);
            }

            IReadOnlyList<string> lengths = head.Headers.GetValues("Content-Length");
            if (lengths.Count > 0)
            {
                // Differing lengths are a request smuggling vector: refuse them.
                if (lengths.SelectMany(v => v.Split(',')).Select(v => v.Trim()).Distinct().Count() != 1 || head.Headers.ContentLength is not { } length)
                    throw new HttpException("The response has an invalid Content-Length.");
                return new ContentLengthStream(reader, length);
            }

            return new UntilCloseStream(reader);
        }

        private Stream Decode(Stream body, HttpHeaders headers)
        {
            if (!Options.AutomaticDecompression || headers.Get("Content-Encoding") is not { } encoding)
                return body;

            Stream decoded = body;
            // Codings are listed in the order they were applied: undo them in reverse.
            foreach (string coding in encoding.Split(',').Select(c => c.Trim().ToLowerInvariant()).Reverse())
            {
                decoded = coding switch
                {
                    "gzip" or "x-gzip" => new GZipStream(decoded, CompressionMode.Decompress),
                    "deflate" => new ZLibStream(decoded, CompressionMode.Decompress),
                    "br" => new BrotliStream(decoded, CompressionMode.Decompress),
                    "identity" or "" => decoded,
                    _ => throw new HttpException($"Unsupported Content-Encoding '{coding}'.")
                };
            }

            return decoded;
        }

        private void StoreCookies(Uri uri, HttpHeaders headers)
        {
            if (Options.Cookies is null)
                return;

            foreach (string cookie in headers.GetValues("Set-Cookie"))
            {
                try
                {
                    Options.Cookies.SetCookies(uri, cookie);
                }
                catch (CookieException)
                {
                    // Invalid cookies are ignored, as browsers do.
                }
            }
        }

        private NetworkConnection? Rent(string key)
        {
            if (!_pool.TryGetValue(key, out var stack))
                return null;

            long now = Environment.TickCount64;
            while (stack.TryPop(out var pooled))
            {
                if (!pooled.Connection.IsDisposed && now - pooled.ReturnedAt < Options.PooledConnectionIdleTimeout.TotalMilliseconds)
                    return pooled.Connection;
                pooled.Connection.Dispose();
            }

            return null;
        }

        private void Return(string key, NetworkConnection connection)
            => _pool.GetOrAdd(key, _ => new()).Push(new PooledConnection(connection, Environment.TickCount64));

        private static Exception Cancelled(CancellationToken callerToken, CancellationTokenSource timeout, Exception inner)
            => callerToken.IsCancellationRequested
                ? new OperationCanceledException("The request was cancelled.", inner, callerToken)
                : new TimeoutException($"The request did not complete within {timeout}.", inner);

        private sealed record PooledConnection(NetworkConnection Connection, long ReturnedAt);

        /// <summary>The connection a request is using right now, so cancellation can abort it.</summary>
        private sealed class RequestContext
        {
            public volatile NetworkConnection? Connection;

            public void Abort() => Connection?.Abort();
        }
    }
}

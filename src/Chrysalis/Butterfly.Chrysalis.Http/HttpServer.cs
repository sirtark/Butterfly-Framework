using Butterfly.Chrysalis.Http.Http2;
using Butterfly.Chrysalis.Http.Internal;
using Butterfly.Communication;
using Butterfly.Networking.Sockets;
using Microsoft.Extensions.Logging;
using System.Net.Security;
using System.Security.Authentication;

namespace Butterfly.Chrysalis.Http
{
    /// <summary>
    /// HTTP server over Butterfly sockets. Requests go to the handler of the longest matching path prefix
    /// (<see cref="Map(string, IHttpHandler)"/>), unless a conditional handler claims them first (<see cref="MapWhen"/>).
    /// Each connection is served by its own thread; requests and responses are buffered in memory within the limits of
    /// <see cref="HttpServerOptions"/>.
    /// </summary>
    public sealed class HttpServer : IAsyncDisposable
    {
        private readonly Lock gate = new();
        private readonly List<(string Prefix, IHttpHandler Handler)> routes = [];
        private readonly List<(Func<HttpServerRequest, bool> Predicate, IHttpHandler Handler)> conditionalRoutes = [];
        private readonly List<(TcpSocket Socket, HttpEndpoint Endpoint, Thread Thread)> listeners = [];
        private readonly Dictionary<long, TcpSocket> connections = [];
        private readonly CancellationTokenSource stopping = new();
        private long nextConnectionId;
        private int activeConnections;

        public HttpServer(HttpServerOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            Options = options;
        }

        public HttpServerOptions Options { get; }

        /// <summary>The endpoints actually listening (with the real port when 0 was requested). Empty until started.</summary>
        public IReadOnlyList<HttpEndpoint> Endpoints { get; private set; } = [];

        public bool IsRunning => Endpoints.Count > 0 && !IsStopping;
        internal bool IsStopping => stopping.IsCancellationRequested;

        /// <summary>Sends requests whose path is <paramref name="pathPrefix"/> or below it ("/api" serves "/api" and "/api/x", not "/apix").</summary>
        public HttpServer Map(string pathPrefix, IHttpHandler handler)
        {
            ArgumentNullException.ThrowIfNull(pathPrefix);
            ArgumentNullException.ThrowIfNull(handler);
            var prefix = "/" + pathPrefix.Trim('/');
            lock (gate)
            {
                if (routes.Exists(route => route.Prefix.Equals(prefix, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException($"The path '{prefix}' is already mapped.");
                routes.Add((prefix, handler));
                routes.Sort((a, b) => b.Prefix.Length.CompareTo(a.Prefix.Length));
            }
            return this;
        }

        public HttpServer Map(string pathPrefix, Func<HttpServerContext, ValueTask> handler) => Map(pathPrefix, new DelegateHttpHandler(handler));

        /// <summary>Sends the requests <paramref name="predicate"/> accepts to <paramref name="handler"/>, before any path prefix.</summary>
        public HttpServer MapWhen(Func<HttpServerRequest, bool> predicate, IHttpHandler handler)
        {
            ArgumentNullException.ThrowIfNull(predicate);
            ArgumentNullException.ThrowIfNull(handler);
            lock (gate)
                conditionalRoutes.Add((predicate, handler));
            return this;
        }

        /// <summary>Binds every endpoint and starts accepting connections.</summary>
        /// <exception cref="SocketException">An endpoint could not be bound (address in use...).</exception>
        public void Start()
        {
            if (Options.Endpoints.Count == 0)
                throw new InvalidOperationException("Add at least one endpoint to HttpServerOptions.Endpoints.");
            if (listeners.Count > 0)
                throw new InvalidOperationException("The server is already started.");

            var bound = new List<HttpEndpoint>();
            try
            {
                foreach (var endpoint in Options.Endpoints)
                {
                    var socket = new TcpSocket(endpoint.Address.Family, new TcpSocketOptions { NoDelay = true });
                    socket.Bind(endpoint.Address);
                    socket.Listen();
                    var actual = new HttpEndpoint(socket.LocalAddress, endpoint.Certificate);
                    var thread = new Thread(() => AcceptLoop(socket, actual)) { IsBackground = true, Name = $"Chrysalis HTTP {actual}" };
                    listeners.Add((socket, actual, thread));
                    bound.Add(actual);
                }
            }
            catch
            {
                foreach (var (socket, _, _) in listeners)
                    socket.Dispose();
                listeners.Clear();
                throw;
            }

            Endpoints = bound;
            foreach (var (_, _, thread) in listeners)
                thread.Start();
        }

        /// <summary>Stops accepting, closes every connection and waits for them to end.</summary>
        public async Task StopAsync()
        {
            if (stopping.IsCancellationRequested)
                return;
            stopping.Cancel();

            foreach (var (socket, _, _) in listeners)
                socket.Abort();
            lock (connections)
            {
                foreach (var connection in connections.Values)
                    connection.Abort();
            }

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (Volatile.Read(ref activeConnections) > 0 && DateTime.UtcNow < deadline)
                await Task.Delay(20).ConfigureAwait(false);
            foreach (var (_, _, thread) in listeners)
                thread.Join(TimeSpan.FromSeconds(5));
        }

        public ValueTask DisposeAsync() => new(StopAsync());

        private void AcceptLoop(TcpSocket listener, HttpEndpoint endpoint)
        {
            while (!stopping.IsCancellationRequested)
            {
                TcpSocket client;
                try
                {
                    client = listener.Accept();
                }
                catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
                {
                    if (stopping.IsCancellationRequested)
                        return;
                    Options.Logger.LogWarning(exception, "Accepting a connection on {Endpoint} failed.", endpoint);
                    continue;
                }

                if (Interlocked.Increment(ref activeConnections) > Options.MaxConcurrentConnections)
                {
                    Interlocked.Decrement(ref activeConnections);
                    client.Dispose();
                    continue;
                }

                var id = Interlocked.Increment(ref nextConnectionId);
                lock (connections)
                    connections[id] = client;
                new Thread(() => Serve(id, client, endpoint)) { IsBackground = true, Name = "Chrysalis HTTP connection" }.Start();
            }
        }

        private void Serve(long id, TcpSocket socket, HttpEndpoint endpoint)
        {
            Stream? stream = null;
            try
            {
                var remote = socket.RemoteAddress.ToString();
                stream = new SocketStream(socket, ownsSocket: false);
                var http2 = false;

                if (endpoint.Certificate is not null)
                {
                    socket.ReceiveTimeout = Options.RequestTimeout;
                    var tls = new SslStream(stream, leaveInnerStreamOpen: false);
                    stream = tls;
                    tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate = endpoint.Certificate,
                        ApplicationProtocols = Options.EnableHttp2 ? [SslApplicationProtocol.Http2, SslApplicationProtocol.Http11] : [SslApplicationProtocol.Http11],
                        ClientCertificateRequired = false
                    }, stopping.Token).GetAwaiter().GetResult();
                    http2 = tls.NegotiatedApplicationProtocol == SslApplicationProtocol.Http2;
                }

                var input = new InputBuffer(stream);
                if (http2)
                    new Http2Connection(this, socket, input, isSecure: true, remote).Run(expectPreface: true);
                else
                    new Http1Connection(this, socket, input, endpoint.IsSecure, remote).Run();
            }
            catch (Exception exception) when (exception is IOException or SocketException or AuthenticationException or ObjectDisposedException or OperationCanceledException)
            {
                // The client went away, timed out or failed the TLS handshake.
            }
            catch (Exception exception)
            {
                Options.Logger.LogError(exception, "An HTTP connection failed.");
            }
            finally
            {
                try
                {
                    stream?.Dispose();
                }
                catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException)
                {
                }
                socket.Dispose();
                lock (connections)
                    connections.Remove(id);
                Interlocked.Decrement(ref activeConnections);
            }
        }

        // HTTP/1.x serves on the connection thread.
        internal HttpServerResponse Dispatch(HttpServerRequest request) => DispatchAsync(request, CancellationToken.None).GetAwaiter().GetResult();

        /// <summary>Runs the handler of a request; failures become a 500 response (logged).</summary>
        internal async Task<HttpServerResponse> DispatchAsync(HttpServerRequest request, CancellationToken aborted)
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(aborted, stopping.Token);
            cancellation.CancelAfter(Options.HandlerTimeout);
            var context = new HttpServerContext(request, cancellation.Token);

            var handler = FindHandler(request);
            if (handler is null)
            {
                context.Response.StatusCode = 404;
                context.Response.Write("Not found.", "text/plain; charset=utf-8");
                return context.Response;
            }

            try
            {
                await handler.HandleAsync(context).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                if (exception is not OperationCanceledException)
                    Options.Logger.LogError(exception, "The handler of {Method} {Path} failed.", request.Method, request.Path);
                context.Response.Reset();
                context.Response.StatusCode = exception is OperationCanceledException ? 503 : 500;
                context.Response.Write(exception is OperationCanceledException ? "The request was cancelled." : "Internal server error.", "text/plain; charset=utf-8");
            }
            return context.Response;
        }

        private IHttpHandler? FindHandler(HttpServerRequest request)
        {
            foreach (var (predicate, handler) in conditionalRoutes)
            {
                if (predicate(request))
                    return handler;
            }

            foreach (var (prefix, handler) in routes)
            {
                if (prefix == "/"
                    || request.Path.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                    || (request.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && request.Path.Length > prefix.Length && request.Path[prefix.Length] == '/'))
                    return handler;
            }
            return null;
        }
    }
}

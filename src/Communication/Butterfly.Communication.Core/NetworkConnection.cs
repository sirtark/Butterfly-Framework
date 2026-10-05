using System.Net.Security;
using System.Security.Authentication;
using System.Text;

using Butterfly.Communication.Dns;
using Butterfly.Networking.Sockets;

namespace Butterfly.Communication
{
    /// <summary>
    /// A TCP connection to a named host: resolution, connection with timeouts, optional TLS (from the start
    /// or upgraded later for STARTTLS), and buffered line reading. Every Butterfly protocol client runs on one.
    /// </summary>
    public sealed class NetworkConnection : IDisposable
    {
        private static readonly TimeSpan MinimumAttemptTimeout = TimeSpan.FromSeconds(3);

        private readonly TcpSocket _socket;
        private Stream _stream;
        private int _disposed;

        private NetworkConnection(string host, int port, TcpSocket socket, SocketAddress remoteAddress, ConnectionOptions options)
        {
            Host = host;
            Port = port;
            RemoteAddress = remoteAddress;
            Options = options;
            _socket = socket;
            _stream = new SocketStream(socket);
            Reader = new ProtocolReader(_stream);
        }

        public string Host { get; }
        public int Port { get; }
        public SocketAddress RemoteAddress { get; }
        public ConnectionOptions Options { get; }

        /// <summary>The stream to write to; reads should go through <see cref="Reader"/>, which buffers.</summary>
        public Stream Stream => _stream;

        public ProtocolReader Reader { get; private set; }

        /// <summary>The TLS session, once the connection is secured.</summary>
        public SslStream? Tls { get; private set; }

        public bool IsSecure => Tls is not null;

        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        /// <summary>Resolves <paramref name="host"/>, connects to the first address that answers and optionally starts TLS.</summary>
        /// <exception cref="ConnectionFailedException">No address could be resolved or reached in time.</exception>
        public static NetworkConnection Connect(string host, int port, ConnectionOptions? options = null, bool useTls = false,
            IList<SslApplicationProtocol>? applicationProtocols = null, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(host);
            ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(port, ushort.MaxValue);
            options ??= ConnectionOptions.Default;

            // Uri.Host keeps the brackets of IPv6 literals.
            string address = host.StartsWith('[') && host.EndsWith(']') ? host[1..^1] : host;
            long deadline = Environment.TickCount64 + (long)options.ConnectTimeout.TotalMilliseconds;

            IReadOnlyList<SocketAddress> candidates;
            try
            {
                candidates = options.EffectiveResolver.Resolve(address, cancellationToken);
            }
            catch (DnsException ex)
            {
                throw new ConnectionFailedException(host, port, ex.Message, ex);
            }

            if (candidates.Count == 0)
                throw new ConnectionFailedException(host, port, "the host has no IPv4 or IPv6 address.");

            Exception? lastError = null;
            for (int i = 0; i < candidates.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                TimeSpan remaining = TimeSpan.FromMilliseconds(deadline - Environment.TickCount64);
                if (remaining <= TimeSpan.Zero)
                    break;

                // Share the budget so an unreachable first address cannot starve the others.
                TimeSpan attempt = i == candidates.Count - 1
                    ? remaining
                    : TimeSpan.FromTicks(Math.Min(remaining.Ticks, Math.Max(MinimumAttemptTimeout.Ticks, remaining.Ticks / (candidates.Count - i))));

                var target = WithPort(candidates[i], (ushort)port);
                var socket = new TcpSocket(target.Family, new TcpSocketOptions { NoDelay = options.NoDelay });
                try
                {
                    // Cancellation is checked between attempts; each attempt is bounded by its own timeout.
                    socket.Connect(target, attempt);

                    socket.ReceiveTimeout = options.ReadTimeout;
                    socket.SendTimeout = options.WriteTimeout;
                }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
                {
                    socket.Dispose();
                    cancellationToken.ThrowIfCancellationRequested();
                    lastError = ex;
                    continue;
                }

                var connection = new NetworkConnection(host, port, socket, target, options);
                try
                {
                    if (useTls)
                        connection.UpgradeToTls(applicationProtocols);
                    return connection;
                }
                catch
                {
                    connection.Dispose();
                    throw;
                }
            }

            throw new ConnectionFailedException(host, port, lastError?.Message ?? "the connection timed out.", lastError);
        }

        /// <summary>
        /// Connects to an exact address, keeping <paramref name="host"/> as the name TLS validates (FTP data channels,
        /// where the address comes from the server but the certificate belongs to the control connection's host).
        /// </summary>
        public static NetworkConnection Connect(SocketAddress address, string host, ConnectionOptions? options = null)
        {
            options ??= ConnectionOptions.Default;
            var socket = new TcpSocket(address.Family, new TcpSocketOptions { NoDelay = options.NoDelay });
            try
            {
                socket.Connect(address, options.ConnectTimeout);
                socket.ReceiveTimeout = options.ReadTimeout;
                socket.SendTimeout = options.WriteTimeout;
            }
            catch (SocketException ex)
            {
                socket.Dispose();
                throw new ConnectionFailedException(address.AddressToString(), address.Port, ex.Message, ex);
            }

            return new NetworkConnection(host, address.Port, socket, address, options);
        }

        public static Task<NetworkConnection> ConnectAsync(string host, int port, ConnectionOptions? options = null, bool useTls = false,
            IList<SslApplicationProtocol>? applicationProtocols = null, CancellationToken cancellationToken = default)
            => Task.Run(() => Connect(host, port, options, useTls, applicationProtocols, cancellationToken), cancellationToken);

        /// <summary>Starts TLS on the connection (implicit TLS, or after a STARTTLS-style command was accepted).</summary>
        /// <exception cref="AuthenticationException">The handshake failed or the server certificate was rejected.</exception>
        public void UpgradeToTls(IList<SslApplicationProtocol>? applicationProtocols = null)
        {
            if (IsSecure)
                throw new InvalidOperationException("The connection is already secured.");

            // Bytes the server sent before the handshake would otherwise be trusted as if they were encrypted
            // (the STARTTLS command injection attack, CVE-2011-0411).
            if (Reader.BufferedCount > 0)
                throw new ProtocolViolationException("The server sent data before the TLS handshake.");

            TlsOptions tls = Options.Tls;
            var ssl = new SslStream(_stream, leaveInnerStreamOpen: false);
            try
            {
                ssl.AuthenticateAsClient(new SslClientAuthenticationOptions
                {
                    TargetHost = Host.Trim('[', ']'),
                    EnabledSslProtocols = tls.EnabledProtocols,
                    ClientCertificates = tls.ClientCertificates,
                    RemoteCertificateValidationCallback = tls.RemoteCertificateValidation,
                    CertificateRevocationCheckMode = tls.RevocationMode,
                    ApplicationProtocols = applicationProtocols is null ? null : [.. applicationProtocols],
                });
            }
            catch
            {
                ssl.Dispose();
                throw;
            }

            _stream = ssl;
            Tls = ssl;
            Reader = new ProtocolReader(ssl);
        }

        public void Write(ReadOnlySpan<byte> data) => _stream.Write(data);

        /// <summary>Writes <paramref name="line"/> followed by CRLF in a single write.</summary>
        public void WriteLine(string line, Encoding? encoding = null)
        {
            encoding ??= Encoding.UTF8;
            byte[] bytes = new byte[encoding.GetByteCount(line) + 2];
            int length = encoding.GetBytes(line, bytes);
            bytes[length] = (byte)'\r';
            bytes[length + 1] = (byte)'\n';
            _stream.Write(bytes);
        }

        public void Flush() => _stream.Flush();

        /// <summary>Runs a blocking protocol operation on the thread pool; cancelling aborts the connection.</summary>
        public Task<T> RunAsync<T>(Func<T> operation, CancellationToken cancellationToken)
            => Task.Run(() =>
            {
                using (cancellationToken.Register(Abort))
                {
                    T result;
                    try
                    {
                        result = operation();
                    }
                    catch (Exception) when (cancellationToken.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(cancellationToken);
                    }

                    // An aborted read may look like a normal end of stream: report the cancellation anyway.
                    cancellationToken.ThrowIfCancellationRequested();
                    return result;
                }
            }, cancellationToken);

        public Task RunAsync(Action operation, CancellationToken cancellationToken)
            => RunAsync(() => { operation(); return true; }, cancellationToken);

        /// <summary>Tears the connection down immediately, waking up blocked reads where the OS allows it.</summary>
        public void Abort()
        {
            _socket.Abort();
            Dispose();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            try
            {
                _stream.Dispose();
            }
            catch (IOException)
            {
                // Closing TLS sends close_notify, which can fail on a broken connection.
            }

            _socket.Dispose();
        }

        private static SocketAddress WithPort(SocketAddress address, ushort port)
            => new(address.GetAddressBytes(), port, address.ScopeId);
    }
}

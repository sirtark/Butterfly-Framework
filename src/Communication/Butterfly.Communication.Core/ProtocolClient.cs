using Butterfly.Networking.Sockets;

namespace Butterfly.Communication
{
    /// <summary>
    /// Base of the connection-oriented clients (SMTP, POP3, IMAP, FTP, MQTT): owns the connection, applies the
    /// <see cref="TlsMode"/> policy and refuses to send credentials in plain text.
    /// </summary>
    public abstract class ProtocolClient : IDisposable
    {
        private NetworkConnection? _connection;

        protected ProtocolClient(ConnectionOptions? options) => Options = options ?? ConnectionOptions.Default;

        public ConnectionOptions Options { get; }

        public bool IsConnected => _connection is { IsDisposed: false };

        public bool IsSecure => _connection?.IsSecure == true;

        public string? Host => _connection?.Host;

        /// <summary>
        /// Allows sending passwords over an unencrypted connection to a remote host. Off by default;
        /// loopback connections are always allowed.
        /// </summary>
        public bool AllowInsecureAuthentication { get; set; }

        protected NetworkConnection Connection
            => _connection is { IsDisposed: false } connection ? connection : throw new InvalidOperationException("The client is not connected.");

        /// <summary>Resolves <see cref="TlsMode.Auto"/>: implicit TLS on the protocol's TLS port, required STARTTLS elsewhere.</summary>
        protected static TlsMode ResolveTlsMode(TlsMode mode, int port, int implicitTlsPort)
            => mode == TlsMode.Auto ? (port == implicitTlsPort ? TlsMode.Implicit : TlsMode.StartTls) : mode;

        protected NetworkConnection OpenConnection(string host, int port, bool implicitTls, CancellationToken cancellationToken)
        {
            if (IsConnected)
                throw new InvalidOperationException("The client is already connected.");

            _connection = NetworkConnection.Connect(host, port, Options, implicitTls, cancellationToken: cancellationToken);
            return _connection;
        }

        /// <summary>Applies the STARTTLS part of the policy once the server has said whether it supports it.</summary>
        /// <param name="startTls">Sends the protocol's STARTTLS command and returns whether the server accepted it.</param>
        protected void NegotiateStartTls(TlsMode mode, bool offered, Func<bool> startTls)
        {
            if (mode is not (TlsMode.StartTls or TlsMode.StartTlsWhenAvailable) || IsSecure)
                return;

            if (!offered)
            {
                if (mode == TlsMode.StartTls)
                    throw new CommunicationException("The server does not support STARTTLS. Use TlsMode.StartTlsWhenAvailable or TlsMode.None to continue without encryption.");
                return;
            }

            if (!startTls())
                throw new CommunicationException("The server refused to start TLS.");

            Connection.UpgradeToTls();
        }

        protected void EnsureCanSendCredentials()
        {
            if (IsSecure || AllowInsecureAuthentication || IsLoopback(Connection.RemoteAddress))
                return;

            throw new InvalidOperationException(
                "Refusing to send credentials over an unencrypted connection. Use TLS, or set AllowInsecureAuthentication if this is intended.");
        }

        protected Task<T> RunAsync<T>(Func<T> operation, CancellationToken cancellationToken)
            => _connection is { IsDisposed: false } connection
                ? connection.RunAsync(operation, cancellationToken)
                : Task.Run(operation, cancellationToken);

        protected Task RunAsync(Action operation, CancellationToken cancellationToken)
            => RunAsync(() => { operation(); return true; }, cancellationToken);

        /// <summary>Closes the connection without saying goodbye to the server.</summary>
        protected void CloseConnection()
        {
            _connection?.Dispose();
            _connection = null;
        }

        /// <summary>Politely ends the session (QUIT, LOGOUT, DISCONNECT...). Errors are ignored.</summary>
        protected virtual void SayGoodbye() { }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposing || !IsConnected)
                return;

            try
            {
                SayGoodbye();
            }
            catch (Exception ex) when (ex is IOException or CommunicationException or InvalidOperationException)
            {
            }
            finally
            {
                CloseConnection();
            }
        }

        private static bool IsLoopback(SocketAddress address)
        {
            byte[] bytes = address.GetAddressBytes();
            return address.Family == AddressFamily.IPv4
                ? bytes[0] == 127
                : bytes.AsSpan(0, 15).IndexOfAnyExcept((byte)0) < 0 && bytes[15] == 1;
        }
    }
}

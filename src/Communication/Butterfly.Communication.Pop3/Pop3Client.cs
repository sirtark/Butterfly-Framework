using System.Globalization;

using Butterfly.Communication.Mail;

namespace Butterfly.Communication.Pop3
{
    public sealed record Pop3MessageInfo(int Number, long Size, string? Uid);

    public class Pop3Exception : CommunicationException
    {
        public Pop3Exception(string message, string? serverMessage = null)
            : base(serverMessage is null ? message : $"{message} Server said: {serverMessage}") => ServerMessage = serverMessage;

        public string? ServerMessage { get; }
    }

    /// <summary>
    /// POP3 client. Messages are numbered from 1 for the current session; use <see cref="Pop3MessageInfo.Uid"/> to
    /// recognise them across sessions. Deletions take effect when the session ends with <see cref="Disconnect"/>.
    /// </summary>
    public sealed class Pop3Client(ConnectionOptions? options = null) : ProtocolClient(options)
    {
        public const int DefaultPort = 110;
        public const int ImplicitTlsPort = 995;

        private List<string> _capabilities = [];

        public IReadOnlyList<string> Capabilities => _capabilities;

        public IReadOnlyList<string> SaslMechanisms
            => _capabilities.FirstOrDefault(c => c.StartsWith("SASL ", StringComparison.OrdinalIgnoreCase))?[5..].Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];

        public string? Greeting { get; private set; }

        public bool IsAuthenticated { get; private set; }

        /// <param name="tls"><see cref="TlsMode.Auto"/>: implicit TLS on 995, required STLS on any other port.</param>
        public void Connect(string host, int port = ImplicitTlsPort, TlsMode tls = TlsMode.Auto, CancellationToken cancellationToken = default)
        {
            TlsMode mode = ResolveTlsMode(tls, port, ImplicitTlsPort);
            OpenConnection(host, port, mode == TlsMode.Implicit, cancellationToken);

            try
            {
                Greeting = Expect(ReadStatus(), "The server did not accept the connection.");
                LoadCapabilities();
                NegotiateStartTls(mode, _capabilities.Contains("STLS", StringComparer.OrdinalIgnoreCase), () => ReadStatusAfter("STLS").Ok);

                if (IsSecure && mode != TlsMode.Implicit)
                    LoadCapabilities();
            }
            catch
            {
                CloseConnection();
                throw;
            }
        }

        public Task ConnectAsync(string host, int port = ImplicitTlsPort, TlsMode tls = TlsMode.Auto, CancellationToken cancellationToken = default)
            => Task.Run(() => Connect(host, port, tls, cancellationToken), cancellationToken);

        /// <summary>SASL PLAIN when the server offers it, otherwise USER/PASS.</summary>
        public void Authenticate(string userName, string password)
        {
            EnsureCanSendCredentials();

            if (SaslMechanisms.Contains("PLAIN", StringComparer.OrdinalIgnoreCase))
            {
                Expect(ReadStatusAfter("AUTH PLAIN " + Sasl.Plain(userName, password)), "Authentication failed.");
            }
            else
            {
                Expect(ReadStatusAfter("USER " + userName), "The user name was rejected.");
                Expect(ReadStatusAfter("PASS " + password), "Authentication failed.");
            }

            IsAuthenticated = true;
        }

        public void AuthenticateOAuth2(string userName, string accessToken)
        {
            EnsureCanSendCredentials();
            var status = ReadStatusAfter("AUTH XOAUTH2 " + Sasl.XOAuth2(userName, accessToken));

            // A "+ <base64 error>" continuation is answered with an empty line to get the final -ERR.
            if (!status.Ok && status.Continuation)
                status = ReadStatusAfter("");

            Expect(status, "Authentication failed.");
            IsAuthenticated = true;
        }

        public Task AuthenticateAsync(string userName, string password, CancellationToken cancellationToken = default)
            => RunAsync(() => Authenticate(userName, password), cancellationToken);

        /// <summary>Number of messages and total size in bytes (STAT).</summary>
        public (int Count, long Size) GetStatus()
        {
            string[] parts = Expect(ReadStatusAfter("STAT"), "STAT failed.").Split(' ');
            return (int.Parse(parts[0], CultureInfo.InvariantCulture), long.Parse(parts[1], CultureInfo.InvariantCulture));
        }

        /// <summary>Every message with its size and, when the server supports UIDL, its unique id.</summary>
        public IReadOnlyList<Pop3MessageInfo> ListMessages()
        {
            Expect(ReadStatusAfter("LIST"), "LIST failed.");
            var sizes = ReadMultiLine().Select(ParsePair).ToDictionary(p => p.Number, p => long.Parse(p.Value, CultureInfo.InvariantCulture));

            Dictionary<int, string> uids = [];
            var uidl = ReadStatusAfter("UIDL");
            if (uidl.Ok)
                uids = ReadMultiLine().Select(ParsePair).ToDictionary(p => p.Number, p => p.Value);

            return [.. sizes.OrderBy(s => s.Key).Select(s => new Pop3MessageInfo(s.Key, s.Value, uids.GetValueOrDefault(s.Key)))];
        }

        /// <summary>The raw RFC 5322 bytes of message <paramref name="number"/> (RETR).</summary>
        public byte[] RetrieveRaw(int number)
        {
            Expect(ReadStatusAfter($"RETR {number}"), $"Message {number} could not be retrieved.");
            return ReadMultiLineBytes();
        }

        public MailMessage Retrieve(int number) => MailMessage.Parse(RetrieveRaw(number));

        /// <summary>The headers and the first <paramref name="bodyLines"/> lines of the body (TOP).</summary>
        public MailMessage RetrieveHeaders(int number, int bodyLines = 0)
        {
            Expect(ReadStatusAfter($"TOP {number} {bodyLines}"), $"The headers of message {number} could not be retrieved.");
            return MailMessage.Parse(ReadMultiLineBytes());
        }

        public Task<MailMessage> RetrieveAsync(int number, CancellationToken cancellationToken = default)
            => RunAsync(() => Retrieve(number), cancellationToken);

        /// <summary>Marks a message for deletion; it is removed when the session ends with <see cref="Disconnect"/>.</summary>
        public void Delete(int number) => Expect(ReadStatusAfter($"DELE {number}"), $"Message {number} could not be deleted.");

        /// <summary>Unmarks every message marked for deletion.</summary>
        public void Reset() => Expect(ReadStatusAfter("RSET"), "RSET failed.");

        public void NoOp() => Expect(ReadStatusAfter("NOOP"), "NOOP failed.");

        /// <summary>Sends QUIT (which applies deletions) and closes the connection.</summary>
        public void Disconnect() => Dispose();

        protected override void SayGoodbye() => ReadStatusAfter("QUIT");

        private void LoadCapabilities()
        {
            _capabilities = ReadStatusAfter("CAPA").Ok ? [.. ReadMultiLine()] : [];
        }

        private Status ReadStatusAfter(string command)
        {
            Connection.WriteLine(command);
            Connection.Flush();
            return ReadStatus();
        }

        private Status ReadStatus()
        {
            string line = Connection.Reader.ReadLine() ?? throw new Pop3Exception("The server closed the connection.");

            if (line.StartsWith("+OK", StringComparison.OrdinalIgnoreCase))
                return new Status(true, false, line.Length > 4 ? line[4..] : "");
            if (line.StartsWith("-ERR", StringComparison.OrdinalIgnoreCase))
                return new Status(false, false, line.Length > 5 ? line[5..] : "");
            if (line.StartsWith('+'))
                return new Status(false, true, line.Length > 2 ? line[2..] : "");

            throw new ProtocolViolationException($"Invalid POP3 status line: '{line}'.");
        }

        private static string Expect(Status status, string message)
            => status.Ok ? status.Text : throw new Pop3Exception(message, status.Text);

        private IEnumerable<string> ReadMultiLine()
        {
            var lines = new List<string>();
            while (true)
            {
                string line = Connection.Reader.ReadLine() ?? throw new Pop3Exception("The server closed the connection.");
                if (line == ".")
                    return lines;
                lines.Add(line.StartsWith("..", StringComparison.Ordinal) ? line[1..] : line);
            }
        }

        /// <summary>Reads a multi-line body byte-exactly (messages may not be UTF-8), undoing the dot-stuffing.</summary>
        private byte[] ReadMultiLineBytes()
        {
            using var output = new MemoryStream();
            while (true)
            {
                byte[] line = Connection.Reader.ReadLineBytes() ?? throw new Pop3Exception("The server closed the connection.");
                if (line is [(byte)'.'])
                    return output.ToArray();

                int skip = line is [(byte)'.', (byte)'.', ..] ? 1 : 0;
                output.Write(line, skip, line.Length - skip);
                output.Write("\r\n"u8);
            }
        }

        private static (int Number, string Value) ParsePair(string line)
        {
            string[] parts = line.Split(' ', 2, StringSplitOptions.TrimEntries);
            return (int.Parse(parts[0], CultureInfo.InvariantCulture), parts.Length > 1 ? parts[1] : "");
        }

        private readonly record struct Status(bool Ok, bool Continuation, string Text);
    }
}

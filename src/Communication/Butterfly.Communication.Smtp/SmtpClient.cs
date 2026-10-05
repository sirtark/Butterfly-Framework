using System.Globalization;
using System.Text;

using Butterfly.Communication.Mail;

namespace Butterfly.Communication.Smtp
{
    public sealed record SmtpResponse(int Code, IReadOnlyList<string> Lines)
    {
        public string Message => string.Join(" ", Lines);

        /// <summary>The RFC 3463 enhanced status code ("5.1.1") when the server includes one.</summary>
        public string? EnhancedCode
            => Lines.Count > 0 && Lines[0].Split(' ')[0] is var first && first.Count(c => c == '.') == 2 && char.IsAsciiDigit(first[0]) ? first : null;

        public bool IsSuccess => Code is >= 200 and < 400;

        public override string ToString() => $"{Code} {Message}";
    }

    public sealed record SmtpSendResult(IReadOnlyList<MailAddress> Accepted, IReadOnlyList<(MailAddress Recipient, SmtpResponse Response)> Rejected, SmtpResponse Response);

    public class SmtpException : CommunicationException
    {
        public SmtpException(string message, SmtpResponse? response = null) : base(response is null ? message : $"{message} Server said: {response}")
            => Response = response;

        public SmtpResponse? Response { get; }
        public int? StatusCode => Response?.Code;
    }

    /// <summary>Every recipient was refused, so nothing was sent.</summary>
    public sealed class SmtpRecipientsRejectedException(IReadOnlyList<(MailAddress Recipient, SmtpResponse Response)> rejected)
        : SmtpException($"The server rejected every recipient ({string.Join(", ", rejected.Select(r => r.Recipient.Address))}).", rejected[0].Response)
    {
        public IReadOnlyList<(MailAddress Recipient, SmtpResponse Response)> Rejected { get; } = rejected;
    }

    /// <summary>
    /// SMTP submission client. Typical use: Connect("smtp.example.com"), Authenticate(user, password), Send(message).
    /// </summary>
    public sealed class SmtpClient(ConnectionOptions? options = null) : ProtocolClient(options)
    {
        public const int SubmissionPort = 587;
        public const int ImplicitTlsPort = 465;
        public const int RelayPort = 25;

        private Dictionary<string, string> _capabilities = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The name announced in EHLO. Defaults to this machine's host name.</summary>
        public string ClientName { get; set; } = GetDefaultClientName();

        /// <summary>EHLO keywords and their parameters ("SIZE" → "35882577", "AUTH" → "PLAIN LOGIN").</summary>
        public IReadOnlyDictionary<string, string> Capabilities => _capabilities;

        public IReadOnlyList<string> AuthenticationMechanisms
            => Capabilities.TryGetValue("AUTH", out string? mechanisms) ? mechanisms.Split(' ', StringSplitOptions.RemoveEmptyEntries) : [];

        public long? MaxMessageSize
            => Capabilities.TryGetValue("SIZE", out string? size) && long.TryParse(size, CultureInfo.InvariantCulture, out long max) && max > 0 ? max : null;

        public bool IsAuthenticated { get; private set; }

        public string? Greeting { get; private set; }

        /// <param name="tls">
        /// <see cref="TlsMode.Auto"/>: implicit TLS on port 465, required STARTTLS on any other port.
        /// </param>
        public void Connect(string host, int port = SubmissionPort, TlsMode tls = TlsMode.Auto, CancellationToken cancellationToken = default)
        {
            TlsMode mode = ResolveTlsMode(tls, port, ImplicitTlsPort);
            OpenConnection(host, port, mode == TlsMode.Implicit, cancellationToken);

            try
            {
                SmtpResponse greeting = ReadResponse();
                if (greeting.Code != 220)
                    throw new SmtpException("The server did not accept the connection.", greeting);
                Greeting = greeting.Message;

                Hello();
                NegotiateStartTls(mode, Capabilities.ContainsKey("STARTTLS"), () => Command("STARTTLS").Code == 220);

                // After STARTTLS the session starts over: capabilities may change (AUTH usually appears only now).
                if (IsSecure && mode != TlsMode.Implicit)
                    Hello();
            }
            catch
            {
                CloseConnection();
                throw;
            }
        }

        public Task ConnectAsync(string host, int port = SubmissionPort, TlsMode tls = TlsMode.Auto, CancellationToken cancellationToken = default)
            => Task.Run(() => Connect(host, port, tls, cancellationToken), cancellationToken);

        /// <summary>Authenticates with the strongest mechanism both sides support (PLAIN, LOGIN or CRAM-MD5).</summary>
        public void Authenticate(string userName, string password)
        {
            EnsureCanSendCredentials();
            IReadOnlyList<string> offered = AuthenticationMechanisms;

            // Over TLS PLAIN is as good as any; without TLS (loopback) prefer the one that does not reveal the password.
            string[] preference = IsSecure ? ["PLAIN", "LOGIN", "CRAM-MD5"] : ["CRAM-MD5", "PLAIN", "LOGIN"];
            string mechanism = preference.FirstOrDefault(m => offered.Contains(m, StringComparer.OrdinalIgnoreCase))
                ?? throw new SmtpException($"The server offers no supported authentication mechanism ({string.Join(", ", offered)}).");

            SmtpResponse response = mechanism switch
            {
                "PLAIN" => Command("AUTH PLAIN " + Sasl.Plain(userName, password)),
                "LOGIN" => Continue(Continue(Command("AUTH LOGIN"), Sasl.LoginStep(userName)), Sasl.LoginStep(password)),
                _ => CramMd5(userName, password),
            };

            FinishAuthentication(response);
        }

        /// <summary>Authenticates with an OAuth 2.0 access token (Gmail, Microsoft 365).</summary>
        public void AuthenticateOAuth2(string userName, string accessToken)
        {
            EnsureCanSendCredentials();
            SmtpResponse response = Command("AUTH XOAUTH2 " + Sasl.XOAuth2(userName, accessToken));

            // On failure the server sends a base64 JSON error as a challenge; an empty line ends the exchange.
            if (response.Code == 334)
                response = Command("");

            FinishAuthentication(response);
        }

        public Task AuthenticateAsync(string userName, string password, CancellationToken cancellationToken = default)
            => RunAsync(() => Authenticate(userName, password), cancellationToken);

        /// <summary>Sends a message to all its recipients (To, Cc and Bcc). Bcc addresses are not written into the message.</summary>
        /// <exception cref="SmtpRecipientsRejectedException">No recipient was accepted.</exception>
        public SmtpSendResult Send(MailMessage message)
        {
            ArgumentNullException.ThrowIfNull(message);
            MailAddress from = message.Sender ?? message.From ?? throw new ArgumentException("The message has no From address.", nameof(message));
            return Send(from, message.AllRecipients, message.ToBytes());
        }

        /// <summary>Sends raw RFC 5322 data with an explicit envelope.</summary>
        public SmtpSendResult Send(MailAddress from, IEnumerable<MailAddress> recipients, ReadOnlySpan<byte> data)
        {
            var to = recipients.Distinct().ToList();
            if (to.Count == 0)
                throw new ArgumentException("There are no recipients.", nameof(recipients));

            bool international = from.IsInternational || to.Any(r => r.IsInternational);
            if (international && !Capabilities.ContainsKey("SMTPUTF8"))
                throw new SmtpException("Internationalized addresses need SMTPUTF8, which the server does not support.");

            if (MaxMessageSize is { } max && data.Length > max)
                throw new SmtpException($"The message is {data.Length} bytes but the server accepts at most {max}.");

            var mailFrom = new StringBuilder($"MAIL FROM:<{from.Address}>");
            if (Capabilities.ContainsKey("SIZE"))
                mailFrom.Append(CultureInfo.InvariantCulture, $" SIZE={data.Length}");
            if (international)
                mailFrom.Append(" SMTPUTF8");

            Expect(Command(mailFrom.ToString()), "The sender was rejected.");

            var accepted = new List<MailAddress>();
            var rejected = new List<(MailAddress, SmtpResponse)>();
            foreach (MailAddress recipient in to)
            {
                SmtpResponse response = Command($"RCPT TO:<{recipient.Address}>");
                if (response.Code is 250 or 251)
                    accepted.Add(recipient);
                else
                    rejected.Add((recipient, response));
            }

            if (accepted.Count == 0)
            {
                Command("RSET");
                throw new SmtpRecipientsRejectedException(rejected);
            }

            SmtpResponse data354 = Command("DATA");
            if (data354.Code != 354)
                throw new SmtpException("The server refused the message data.", data354);

            WriteDotStuffed(data);
            SmtpResponse final = ReadResponse();
            Expect(final, "The server rejected the message.");

            return new SmtpSendResult(accepted, rejected, final);
        }

        public Task<SmtpSendResult> SendAsync(MailMessage message, CancellationToken cancellationToken = default)
            => RunAsync(() => Send(message), cancellationToken);

        public void NoOp() => Expect(Command("NOOP"), "NOOP failed.");

        /// <summary>Aborts the current mail transaction.</summary>
        public void Reset() => Expect(Command("RSET"), "RSET failed.");

        /// <summary>Sends QUIT and closes the connection.</summary>
        public void Disconnect() => Dispose();

        protected override void SayGoodbye() => Command("QUIT");

        private void Hello()
        {
            SmtpResponse response = Command($"EHLO {ClientName}");
            _capabilities = new(StringComparer.OrdinalIgnoreCase);

            if (response.Code == 250)
            {
                // The first line is the greeting; each following line is "KEYWORD params".
                foreach (string line in response.Lines.Skip(1))
                {
                    string[] parts = line.Split(' ', 2);
                    _capabilities[parts[0]] = parts.Length > 1 ? parts[1] : "";
                }
                return;
            }

            // Pre-ESMTP servers only know HELO.
            Expect(Command($"HELO {ClientName}"), "The server rejected HELO.");
        }

        private SmtpResponse CramMd5(string userName, string password)
        {
            SmtpResponse challenge = Command("AUTH CRAM-MD5");
            if (challenge.Code != 334)
                return challenge;
            return Command(Sasl.CramMd5(challenge.Message, userName, password));
        }

        private SmtpResponse Continue(SmtpResponse challenge, string answer)
            => challenge.Code == 334 ? Command(answer) : challenge;

        private void FinishAuthentication(SmtpResponse response)
        {
            if (response.Code != 235)
                throw new SmtpException("Authentication failed.", response);
            IsAuthenticated = true;
        }

        private SmtpResponse Command(string command)
        {
            Connection.WriteLine(command);
            Connection.Flush();
            return ReadResponse();
        }

        private SmtpResponse ReadResponse()
        {
            var lines = new List<string>();
            int code = 0;

            while (true)
            {
                string line = Connection.Reader.ReadLine()
                    ?? throw new SmtpException("The server closed the connection.");

                if (line.Length < 3 || !int.TryParse(line.AsSpan(0, 3), NumberStyles.None, CultureInfo.InvariantCulture, out int lineCode)
                    || (line.Length > 3 && line[3] is not (' ' or '-')))
                    throw new ProtocolViolationException($"Invalid SMTP reply line: '{line}'.");

                if (code != 0 && lineCode != code)
                    throw new ProtocolViolationException("An SMTP multi-line reply changed its status code.");

                code = lineCode;
                lines.Add(line.Length > 4 ? line[4..] : "");

                if (line.Length == 3 || line[3] == ' ')
                    return new SmtpResponse(code, lines);
            }
        }

        private static void Expect(SmtpResponse response, string message)
        {
            if (!response.IsSuccess)
                throw new SmtpException(message, response);
        }

        /// <summary>Writes the DATA section: lines starting with "." get another one, bare LF becomes CRLF, ends with CRLF.CRLF.</summary>
        private void WriteDotStuffed(ReadOnlySpan<byte> data)
        {
            var output = new MemoryStream(data.Length + data.Length / 50 + 8);
            bool lineStart = true;

            for (int i = 0; i < data.Length; i++)
            {
                byte b = data[i];
                if (lineStart && b == '.')
                    output.WriteByte((byte)'.');

                if (b == '\n' && (i == 0 || data[i - 1] != '\r'))
                    output.WriteByte((byte)'\r');

                output.WriteByte(b);
                lineStart = b == '\n';
            }

            if (!lineStart)
                output.Write("\r\n"u8);
            output.Write(".\r\n"u8);

            Connection.Write(output.GetBuffer().AsSpan(0, (int)output.Length));
            Connection.Flush();
        }

        private static string GetDefaultClientName()
        {
            try
            {
                string name = System.Net.Dns.GetHostName();
                return name.Length > 0 && MimeEncoding.IsAscii(name) ? name : "localhost";
            }
            catch (System.Net.Sockets.SocketException)
            {
                return "localhost";
            }
        }
    }
}

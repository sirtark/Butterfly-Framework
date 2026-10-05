using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using Butterfly.Networking.Sockets;

namespace Butterfly.Communication.Ftp
{
    /// <summary>
    /// FTP client in passive mode. Typical use: Connect("ftp.example.com"), Login(user, password), List(), Download(...).
    /// Transfers are always binary (TYPE I).
    /// </summary>
    public sealed partial class FtpClient(ConnectionOptions? options = null) : ProtocolClient(options)
    {
        public const int DefaultPort = 21;
        public const int ImplicitTlsPort = 990;

        private HashSet<string> _features = new(StringComparer.OrdinalIgnoreCase);
        private bool _protectData;
        private Encoding _encoding = Encoding.UTF8;

        [GeneratedRegex(@"\((\d+),(\d+),(\d+),(\d+),(\d+),(\d+)\)?", RegexOptions.CultureInvariant)]
        private static partial Regex PassiveAddress();

        /// <summary>FEAT keywords (MLST, SIZE, MDTM, UTF8, EPSV, REST STREAM...).</summary>
        public IReadOnlySet<string> Features => _features;

        public string? Greeting { get; private set; }

        public bool IsAuthenticated { get; private set; }

        /// <summary>
        /// Connect data channels to the address announced by PASV. Off by default: servers behind NAT often announce
        /// a private address, and trusting it lets a malicious server aim the client elsewhere (FTP bounce).
        /// </summary>
        public bool UsePassiveAddressFromServer { get; set; }

        /// <param name="tls"><see cref="TlsMode.Auto"/>: implicit FTPS on 990, required AUTH TLS on any other port.</param>
        public void Connect(string host, int port = DefaultPort, TlsMode tls = TlsMode.Auto, CancellationToken cancellationToken = default)
        {
            TlsMode mode = ResolveTlsMode(tls, port, ImplicitTlsPort);
            OpenConnection(host, port, mode == TlsMode.Implicit, cancellationToken);

            try
            {
                FtpResponse greeting = ReadResponse();
                while (greeting.Code == 120) // "service ready in n minutes"
                    greeting = ReadResponse();
                if (greeting.Code != 220)
                    throw new FtpException("The server refused the connection.", greeting);
                Greeting = greeting.Message;

                LoadFeatures();
                // Servers without FEAT may still support AUTH TLS: try it when nothing says otherwise.
                NegotiateStartTls(mode, _features.Count == 0 || _features.Contains("AUTH"), () => Command("AUTH TLS").Code == 234);
                _protectData = IsSecure;
            }
            catch
            {
                CloseConnection();
                throw;
            }
        }

        public Task ConnectAsync(string host, int port = DefaultPort, TlsMode tls = TlsMode.Auto, CancellationToken cancellationToken = default)
            => Task.Run(() => Connect(host, port, tls, cancellationToken), cancellationToken);

        /// <summary>Logs in (anonymous by default) and prepares binary transfers.</summary>
        public void Login(string userName = "anonymous", string password = "anonymous@")
        {
            if (!userName.Equals("anonymous", StringComparison.OrdinalIgnoreCase) && !userName.Equals("ftp", StringComparison.OrdinalIgnoreCase))
                EnsureCanSendCredentials();

            FtpResponse response = Command("USER " + userName);
            if (response.Code == 331)
                response = Command("PASS " + password);
            if (response.Code != 230 && response.Code != 202)
                throw new FtpException("Login failed.", response);

            IsAuthenticated = true;

            // RFC 4217: protect the data channel too once the control channel is encrypted.
            if (_protectData)
            {
                Command("PBSZ 0");
                Expect(Command("PROT P"), "The server refused to protect the data channel.");
            }

            if (_features.Contains("UTF8"))
                Command("OPTS UTF8 ON");

            Expect(Command("TYPE I"), "The server refused binary mode.");
        }

        public Task LoginAsync(string userName = "anonymous", string password = "anonymous@", CancellationToken cancellationToken = default)
            => RunAsync(() => Login(userName, password), cancellationToken);

        public string GetWorkingDirectory()
        {
            FtpResponse response = Expect(Command("PWD"), "PWD failed.");

            // 257 "/path with ""quotes""" is current directory
            string message = response.Message;
            int start = message.IndexOf('"');
            if (start < 0)
                return message;

            var path = new StringBuilder();
            for (int i = start + 1; i < message.Length; i++)
            {
                if (message[i] == '"')
                {
                    if (i + 1 < message.Length && message[i + 1] == '"')
                    {
                        path.Append('"');
                        i++;
                        continue;
                    }
                    break;
                }
                path.Append(message[i]);
            }
            return path.ToString();
        }

        public void ChangeDirectory(string path) => Expect(Command("CWD " + path), $"Cannot change to '{path}'.");

        public void ChangeToParentDirectory() => Expect(Command("CDUP"), "Cannot change to the parent directory.");

        public void CreateDirectory(string path) => Expect(Command("MKD " + path), $"Cannot create '{path}'.");

        public void DeleteDirectory(string path) => Expect(Command("RMD " + path), $"Cannot delete the directory '{path}'.");

        public void DeleteFile(string path) => Expect(Command("DELE " + path), $"Cannot delete '{path}'.");

        public void Rename(string from, string to)
        {
            FtpResponse response = Command("RNFR " + from);
            if (response.Code != 350)
                throw new FtpException($"Cannot rename '{from}'.", response);
            Expect(Command("RNTO " + to), $"Cannot rename '{from}' to '{to}'.");
        }

        /// <summary>The size in bytes (SIZE), or null when the server does not support it or the file does not exist.</summary>
        public long? GetFileSize(string path)
        {
            FtpResponse response = Command("SIZE " + path);
            return response.Code == 213 && long.TryParse(response.Message.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long size) ? size : null;
        }

        /// <summary>The last modification time in UTC (MDTM), or null when unavailable.</summary>
        public DateTimeOffset? GetModifiedTime(string path)
        {
            FtpResponse response = Command("MDTM " + path);
            return response.Code == 213 ? FtpListParser.ParseTimeVal(response.Message.Trim()) : null;
        }

        /// <summary>Lists a directory: MLSD when the server supports it (precise), otherwise LIST (parsed best-effort).</summary>
        public IReadOnlyList<FtpListItem> List(string? path = null)
        {
            bool machine = _features.Contains("MLST");
            string command = (machine ? "MLSD" : "LIST") + (path is null ? "" : " " + path);

            string text = _encoding.GetString(DownloadListing(command));
            var items = new List<FtpListItem>();
            foreach (string line in text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries))
            {
                FtpListItem? item = machine ? FtpListParser.ParseMlsd(line) : FtpListParser.ParseList(line);
                if (item is not null && item.Name is not ("." or ".."))
                    items.Add(item);
            }
            return items;
        }

        /// <summary>Only the names in a directory (NLST).</summary>
        public IReadOnlyList<string> ListNames(string? path = null)
            => _encoding.GetString(DownloadListing("NLST" + (path is null ? "" : " " + path))).Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);

        public Task<IReadOnlyList<FtpListItem>> ListAsync(string? path = null, CancellationToken cancellationToken = default)
            => RunAsync(() => List(path), cancellationToken);

        /// <summary>Downloads a file into <paramref name="destination"/>, optionally resuming at <paramref name="offset"/> (REST).</summary>
        public void Download(string remotePath, Stream destination, long offset = 0)
        {
            if (offset > 0)
            {
                FtpResponse rest = Command("REST " + offset.ToString(CultureInfo.InvariantCulture));
                if (rest.Code != 350)
                    throw new FtpException("The server cannot resume transfers.", rest);
            }

            TransferTo("RETR " + remotePath, destination);
        }

        public byte[] DownloadBytes(string remotePath)
        {
            using var memory = new MemoryStream();
            Download(remotePath, memory);
            return memory.ToArray();
        }

        /// <summary>Downloads to a local file; an existing partial file is resumed when <paramref name="resume"/> is set.</summary>
        public void DownloadFile(string remotePath, string localPath, bool resume = false)
        {
            using var file = new FileStream(localPath, resume ? FileMode.Append : FileMode.Create, FileAccess.Write);
            Download(remotePath, file, resume ? file.Length : 0);
        }

        public Task DownloadFileAsync(string remotePath, string localPath, bool resume = false, CancellationToken cancellationToken = default)
            => RunAsync(() => DownloadFile(remotePath, localPath, resume), cancellationToken);

        /// <summary>Uploads <paramref name="source"/> (STOR), or appends to the remote file (APPE).</summary>
        public void Upload(Stream source, string remotePath, bool append = false)
            => RunTransfer((append ? "APPE " : "STOR ") + remotePath, data => source.CopyTo(data.Stream));

        public void UploadBytes(byte[] data, string remotePath) => Upload(new MemoryStream(data), remotePath);

        public void UploadFile(string localPath, string remotePath)
        {
            using var file = File.OpenRead(localPath);
            Upload(file, remotePath);
        }

        public Task UploadFileAsync(string localPath, string remotePath, CancellationToken cancellationToken = default)
            => RunAsync(() => UploadFile(localPath, remotePath), cancellationToken);

        public void NoOp() => Expect(Command("NOOP"), "NOOP failed.");

        /// <summary>Sends QUIT and closes the connection.</summary>
        public void Disconnect() => Dispose();

        protected override void SayGoodbye() => Command("QUIT");

        // --- transfers ---------------------------------------------------------------------------------------

        private byte[] DownloadListing(string command)
        {
            using var memory = new MemoryStream();
            TransferTo(command, memory);
            return memory.ToArray();
        }

        private void TransferTo(string command, Stream destination)
            => RunTransfer(command, data => data.Reader.AsStream().CopyTo(destination));

        /// <summary>Passive data connection → command → 1xx → (TLS) → data → close → 2xx.</summary>
        private void RunTransfer(string command, Action<NetworkConnection> transfer)
        {
            NetworkConnection data = OpenPassiveConnection();
            try
            {
                FtpResponse start = Command(command);
                if (start.Code is not (125 or 150))
                    throw new FtpException($"'{command.Split(' ')[0]}' failed.", start);

                if (_protectData)
                    data.UpgradeToTls();

                transfer(data);
            }
            finally
            {
                // Closing the data connection (with close_notify under TLS) marks the end of the transfer.
                data.Dispose();
            }

            Expect(ReadResponse(), $"The transfer of '{command}' did not complete.");
        }

        private NetworkConnection OpenPassiveConnection()
        {
            SocketAddress control = Connection.RemoteAddress;

            // EPSV works for IPv4 and IPv6 and only returns a port: the host is always the control connection's.
            FtpResponse epsv = _features.Contains("EPSV") || control.Family == AddressFamily.IPv6 ? Command("EPSV") : new FtpResponse(500, []);
            if (epsv.Code == 229)
            {
                // 229 Entering Extended Passive Mode (|||6446|)
                string message = epsv.Message;
                int open = message.IndexOf('(');
                string[] fields = message[(open + 2)..message.IndexOf(')', open)].Split(message[open + 1]);
                ushort port = ushort.Parse(fields[2], CultureInfo.InvariantCulture);
                return NetworkConnection.Connect(new SocketAddress(control.GetAddressBytes(), port, control.ScopeId), Connection.Host, Options);
            }

            FtpResponse pasv = Expect(Command("PASV"), "The server refused passive mode.");
            Match match = PassiveAddress().Match(pasv.Message);
            if (!match.Success)
                throw new FtpException("Could not understand the PASV reply.", pasv);

            byte[] address = [.. Enumerable.Range(1, 4).Select(i => byte.Parse(match.Groups[i].Value, CultureInfo.InvariantCulture))];
            ushort pasvPort = (ushort)(int.Parse(match.Groups[5].Value, CultureInfo.InvariantCulture) * 256 + int.Parse(match.Groups[6].Value, CultureInfo.InvariantCulture));

            SocketAddress target = UsePassiveAddressFromServer || control.Family != AddressFamily.IPv4
                ? new SocketAddress(address, pasvPort)
                : new SocketAddress(control.GetAddressBytes(), pasvPort);

            return NetworkConnection.Connect(target, Connection.Host, Options);
        }

        // --- control channel ---------------------------------------------------------------------------------

        private void LoadFeatures()
        {
            FtpResponse response = Command("FEAT");
            _features = response.Code == 211
                ? new(response.Lines.Skip(1).Take(Math.Max(0, response.Lines.Count - 2)).Select(l => l.Trim()).Where(l => l.Length > 0)
                    .SelectMany(l => new[] { l, l.Split(' ')[0] }), StringComparer.OrdinalIgnoreCase)
                : new(StringComparer.OrdinalIgnoreCase);

            _encoding = _features.Contains("UTF8") ? Encoding.UTF8 : Encoding.Latin1;
        }

        private FtpResponse Command(string command)
        {
            if (command.AsSpan().IndexOfAny('\r', '\n') >= 0)
                throw new ArgumentException("FTP commands and paths cannot contain line breaks.", nameof(command));

            Connection.WriteLine(command, _encoding);
            Connection.Flush();
            return ReadResponse();
        }

        private FtpResponse ReadResponse()
        {
            string first = Connection.Reader.ReadLine(_encoding) ?? throw new FtpException("The server closed the connection.");
            if (first.Length < 3 || !int.TryParse(first.AsSpan(0, 3), NumberStyles.None, CultureInfo.InvariantCulture, out int code))
                throw new ProtocolViolationException($"Invalid FTP reply: '{first}'.");

            var lines = new List<string> { first.Length > 4 ? first[4..] : "" };

            // Multi-line: "123-first", any lines, "123 last".
            if (first.Length > 3 && first[3] == '-')
            {
                string end = first[..3] + " ";
                while (true)
                {
                    string line = Connection.Reader.ReadLine(_encoding) ?? throw new FtpException("The server closed the connection.");
                    if (line.StartsWith(end, StringComparison.Ordinal) || line == first[..3])
                    {
                        lines.Add(line.Length > 4 ? line[4..] : "");
                        break;
                    }
                    lines.Add(line);
                }
            }

            return new FtpResponse(code, lines);
        }

        private static FtpResponse Expect(FtpResponse response, string message)
            => response.IsPositive ? response : throw new FtpException(message, response);
    }
}

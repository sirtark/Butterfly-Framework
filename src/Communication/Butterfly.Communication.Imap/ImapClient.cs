using System.Globalization;
using System.Text;

using Butterfly.Communication.Mail;

namespace Butterfly.Communication.Imap
{
    /// <summary>
    /// IMAP4rev1 client. Messages are addressed by UID (stable within a folder while its UIDVALIDITY does not change).
    /// Typical use: Connect, Authenticate, Select("INBOX"), Search(ImapQuery.Unseen), Fetch(uid).
    /// </summary>
    public sealed class ImapClient(ConnectionOptions? options = null) : ProtocolClient(options)
    {
        public const int DefaultPort = 143;
        public const int ImplicitTlsPort = 993;

        private HashSet<string> _capabilities = new(StringComparer.OrdinalIgnoreCase);
        private int _tag;

        public IReadOnlySet<string> Capabilities => _capabilities;

        public string? Greeting { get; private set; }

        public bool IsAuthenticated { get; private set; }

        /// <summary>The folder opened with <see cref="Select"/>.</summary>
        public ImapMailboxStatus? SelectedFolder { get; private set; }

        /// <param name="tls"><see cref="TlsMode.Auto"/>: implicit TLS on 993, required STARTTLS on any other port.</param>
        public void Connect(string host, int port = ImplicitTlsPort, TlsMode tls = TlsMode.Auto, CancellationToken cancellationToken = default)
        {
            TlsMode mode = ResolveTlsMode(tls, port, ImplicitTlsPort);
            OpenConnection(host, port, mode == TlsMode.Implicit, cancellationToken);

            try
            {
                List<object?> greeting = ImapParser.ReadResponse(Connection.Reader);
                string status = ImapParser.AsString(greeting.ElementAtOrDefault(1)) ?? "";
                if (status is not ("OK" or "PREAUTH"))
                    throw new ImapException("The server refused the connection.", status, Text(greeting, 2));

                Greeting = Text(greeting, 2);
                IsAuthenticated = status == "PREAUTH";
                LoadCapabilities();

                NegotiateStartTls(mode, _capabilities.Contains("STARTTLS"), () => Execute("STARTTLS").Ok);
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

        /// <summary>AUTHENTICATE PLAIN when offered, otherwise LOGIN.</summary>
        public void Authenticate(string userName, string password)
        {
            EnsureCanSendCredentials();

            Result result = _capabilities.Contains("AUTH=PLAIN")
                ? Sasl("PLAIN", Communication.Sasl.Plain(userName, password))
                : _capabilities.Contains("LOGINDISABLED")
                    ? throw new ImapException("The server disabled LOGIN and offers no supported SASL mechanism.")
                    : Execute(["LOGIN ", new ImapString(userName), " ", new ImapString(password)]);

            FinishAuthentication(result);
        }

        /// <summary>AUTHENTICATE XOAUTH2 with an OAuth 2.0 access token (Gmail, Microsoft 365).</summary>
        public void AuthenticateOAuth2(string userName, string accessToken)
        {
            EnsureCanSendCredentials();
            FinishAuthentication(Sasl("XOAUTH2", Communication.Sasl.XOAuth2(userName, accessToken)));
        }

        public Task AuthenticateAsync(string userName, string password, CancellationToken cancellationToken = default)
            => RunAsync(() => Authenticate(userName, password), cancellationToken);

        public IReadOnlyList<ImapFolder> ListFolders(string reference = "", string pattern = "*")
        {
            Result result = Expect(Execute(["LIST ", new ImapString(reference), " ", new ImapString(ImapFolderName.Encode(pattern))]), "LIST failed.");

            return [.. result.Untagged
                .Where(r => IsKeyword(r, 1, "LIST"))
                .Select(r => new ImapFolder(
                    ImapFolderName.Decode(ImapParser.AsString(r.ElementAtOrDefault(4)) ?? ""),
                    ImapParser.AsString(r.ElementAtOrDefault(3)) is { Length: > 0 } delimiter ? delimiter[0] : null,
                    [.. ImapParser.AsList(r.ElementAtOrDefault(2)).Select(ImapParser.AsString).OfType<string>()]))];
        }

        /// <summary>Opens a folder; with <paramref name="readOnly"/> (EXAMINE) flags such as \Seen are never changed.</summary>
        public ImapMailboxStatus Select(string folder, bool readOnly = false)
        {
            Result result = Expect(Execute([readOnly ? "EXAMINE " : "SELECT ", new ImapString(ImapFolderName.Encode(folder))]), $"Folder '{folder}' could not be opened.");

            int exists = 0, recent = 0;
            uint uidValidity = 0;
            uint? uidNext = null;
            IReadOnlyList<string> flags = [], permanentFlags = [];

            foreach (List<object?> response in result.Untagged)
            {
                string? second = ImapParser.AsString(response.ElementAtOrDefault(2));
                if (second == "EXISTS")
                    exists = ParseInt(response[1]);
                else if (second == "RECENT")
                    recent = ParseInt(response[1]);
                else if (IsKeyword(response, 1, "FLAGS"))
                    flags = [.. ImapParser.AsList(response.ElementAtOrDefault(2)).Select(ImapParser.AsString).OfType<string>()];
                else if (IsKeyword(response, 1, "OK") && ResponseCode(response) is { } code)
                {
                    if (code.Name == "UIDVALIDITY")
                        uidValidity = uint.Parse(code.Arguments, CultureInfo.InvariantCulture);
                    else if (code.Name == "UIDNEXT")
                        uidNext = uint.Parse(code.Arguments, CultureInfo.InvariantCulture);
                    else if (code.Name == "PERMANENTFLAGS")
                        permanentFlags = code.Arguments.Trim('(', ')').Split(' ', StringSplitOptions.RemoveEmptyEntries);
                }
            }

            bool isReadOnly = readOnly || result.Text.Contains("[READ-ONLY]", StringComparison.OrdinalIgnoreCase);
            SelectedFolder = new ImapMailboxStatus(folder, exists, recent, uidValidity, uidNext, flags, permanentFlags, isReadOnly);
            return SelectedFolder;
        }

        public void CreateFolder(string folder) => Expect(Execute(["CREATE ", new ImapString(ImapFolderName.Encode(folder))]), $"Folder '{folder}' could not be created.");

        public void DeleteFolder(string folder) => Expect(Execute(["DELETE ", new ImapString(ImapFolderName.Encode(folder))]), $"Folder '{folder}' could not be deleted.");

        public void RenameFolder(string folder, string newName)
            => Expect(Execute(["RENAME ", new ImapString(ImapFolderName.Encode(folder)), " ", new ImapString(ImapFolderName.Encode(newName))]), $"Folder '{folder}' could not be renamed.");

        /// <summary>UIDs of the messages of the selected folder that match <paramref name="query"/>.</summary>
        public IReadOnlyList<uint> Search(ImapQuery query)
        {
            RequireSelected();
            List<object> command = ["UID SEARCH "];
            if (query.NeedsUtf8)
                command.Add("CHARSET UTF-8 ");
            command.AddRange(query.Parts);

            Result result = Expect(Execute(command), "SEARCH failed.");
            return [.. result.Untagged
                .Where(r => IsKeyword(r, 1, "SEARCH"))
                .SelectMany(r => r.Skip(2))
                .Select(ImapParser.AsString)
                .Where(s => s is not null && s.All(char.IsAsciiDigit))
                .Select(s => uint.Parse(s!, CultureInfo.InvariantCulture))];
        }

        /// <summary>Flags, size, date and headers of the given messages, without downloading their bodies.</summary>
        public IReadOnlyList<ImapMessageSummary> FetchSummaries(IEnumerable<uint> uids)
        {
            string set = UidSet(uids);
            if (set.Length == 0)
                return [];

            Result result = Expect(Execute($"UID FETCH {set} (UID FLAGS RFC822.SIZE INTERNALDATE BODY.PEEK[HEADER])"), "FETCH failed.");
            return [.. FetchItems(result).Select(items => new ImapMessageSummary
            {
                Uid = items.TryGetValue("UID", out object? uid) ? (uint)ParseInt(uid) : 0,
                Flags = [.. ImapParser.AsList(items.GetValueOrDefault("FLAGS")).Select(ImapParser.AsString).OfType<string>()],
                Size = items.TryGetValue("RFC822.SIZE", out object? size) ? ParseInt(size) : 0,
                InternalDate = ParseInternalDate(ImapParser.AsString(items.GetValueOrDefault("INTERNALDATE"))),
                Headers = items.GetValueOrDefault("BODY[HEADER]") is byte[] header ? MailMessage.Parse(header) : null,
            })];
        }

        /// <summary>The raw RFC 5322 bytes of a message. Uses BODY.PEEK, so the message is not marked as read.</summary>
        public byte[] FetchRaw(uint uid)
        {
            Result result = Expect(Execute($"UID FETCH {uid} (UID BODY.PEEK[])"), $"Message {uid} could not be fetched.");
            foreach (var items in FetchItems(result))
            {
                if (items.GetValueOrDefault("BODY[]") is byte[] body)
                    return body;
                if (items.GetValueOrDefault("BODY[]") is string text)
                    return Encoding.UTF8.GetBytes(text);
            }

            throw new ImapException($"Message {uid} does not exist in the selected folder.");
        }

        public MailMessage Fetch(uint uid) => MailMessage.Parse(FetchRaw(uid));

        public Task<MailMessage> FetchAsync(uint uid, CancellationToken cancellationToken = default)
            => RunAsync(() => Fetch(uid), cancellationToken);

        public void AddFlags(IEnumerable<uint> uids, params string[] flags) => Store(uids, "+FLAGS.SILENT", flags);

        public void RemoveFlags(IEnumerable<uint> uids, params string[] flags) => Store(uids, "-FLAGS.SILENT", flags);

        public void MarkAsRead(IEnumerable<uint> uids) => AddFlags(uids, ImapFlags.Seen);

        /// <summary>Flags messages as \Deleted; <see cref="Expunge"/> removes them.</summary>
        public void Delete(IEnumerable<uint> uids) => AddFlags(uids, ImapFlags.Deleted);

        /// <summary>Permanently removes the messages flagged \Deleted from the selected folder.</summary>
        public void Expunge()
        {
            RequireSelected();
            Expect(Execute("EXPUNGE"), "EXPUNGE failed.");
        }

        public void Copy(IEnumerable<uint> uids, string destination)
        {
            RequireSelected();
            Expect(Execute([$"UID COPY {UidSet(uids)} ", new ImapString(ImapFolderName.Encode(destination))]), $"Messages could not be copied to '{destination}'.");
        }

        /// <summary>Moves messages (MOVE when supported, otherwise COPY + delete + expunge).</summary>
        public void Move(IEnumerable<uint> uids, string destination)
        {
            RequireSelected();
            var list = uids.ToList();

            if (_capabilities.Contains("MOVE"))
            {
                Expect(Execute([$"UID MOVE {UidSet(list)} ", new ImapString(ImapFolderName.Encode(destination))]), $"Messages could not be moved to '{destination}'.");
                return;
            }

            Copy(list, destination);
            Delete(list);
            Expect(_capabilities.Contains("UIDPLUS") ? Execute($"UID EXPUNGE {UidSet(list)}") : Execute("EXPUNGE"), "EXPUNGE failed.");
        }

        /// <summary>Stores a message in a folder (a sent copy, a draft...). Returns its UID when the server reports it (UIDPLUS).</summary>
        public uint? Append(string folder, MailMessage message, IEnumerable<string>? flags = null, DateTimeOffset? date = null)
            => Append(folder, message.ToBytes(), flags, date ?? message.Date);

        public uint? Append(string folder, byte[] message, IEnumerable<string>? flags = null, DateTimeOffset? date = null)
        {
            var command = new List<object> { "APPEND ", new ImapString(ImapFolderName.Encode(folder)) };
            if (flags?.ToList() is { Count: > 0 } flagList)
                command.Add($" ({string.Join(' ', flagList)})");
            if (date is { } when)
                command.Add($" \"{FormatInternalDate(when)}\"");
            command.Add(" ");
            command.Add(new ImapLiteral(message));

            Result result = Expect(Execute(command), $"The message could not be stored in '{folder}'.");

            // [APPENDUID <uidvalidity> <uid>]
            int index = result.Text.IndexOf("[APPENDUID ", StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                string[] parts = result.Text[(index + 11)..].Split(' ', ']');
                if (parts.Length > 1 && uint.TryParse(parts[1], CultureInfo.InvariantCulture, out uint uid))
                    return uid;
            }
            return null;
        }

        public void NoOp() => Expect(Execute("NOOP"), "NOOP failed.");

        /// <summary>Sends LOGOUT and closes the connection.</summary>
        public void Disconnect() => Dispose();

        protected override void SayGoodbye() => Execute("LOGOUT");

        // --- protocol engine ---------------------------------------------------------------------------------

        private Result Execute(string command) => Execute([command]);

        /// <summary>Sends a tagged command made of text, <see cref="ImapString"/> and <see cref="ImapLiteral"/> parts.</summary>
        private Result Execute(IReadOnlyList<object> parts)
        {
            string tag = "A" + (++_tag).ToString("D4", CultureInfo.InvariantCulture);
            bool literalPlus = _capabilities.Contains("LITERAL+");
            var pending = new StringBuilder(tag).Append(' ');
            var untagged = new List<List<object?>>();

            foreach (object part in parts)
            {
                byte[]? literal = null;
                switch (part)
                {
                    case string text:
                        pending.Append(text);
                        continue;
                    case ImapString value when IsAtom(value.Value):
                        pending.Append(value.Value);
                        continue;
                    case ImapString value when MimeEncoding.IsAscii(value.Value) && value.Value.IndexOfAny(['\r', '\n', '\0']) < 0:
                        pending.Append('"').Append(value.Value.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"');
                        continue;
                    case ImapString value:
                        literal = value.Utf8;
                        break;
                    case ImapLiteral data:
                        literal = data.Data;
                        break;
                }

                // Literal: "{n}" then wait for "+" (or "{n+}" and go on with LITERAL+), then the raw bytes.
                pending.Append('{').Append(literal!.Length.ToString(CultureInfo.InvariantCulture)).Append(literalPlus ? "+}" : "}");
                Connection.WriteLine(pending.ToString());
                Connection.Flush();
                pending.Clear();

                if (!literalPlus)
                {
                    WaitForContinuation(tag, untagged, out Result? refused);
                    if (refused is not null)
                        return refused;
                }

                Connection.Write(literal);
            }

            Connection.WriteLine(pending.ToString());
            Connection.Flush();
            return ReadUntilTagged(tag, untagged);
        }

        /// <summary>Waits for "+"; if the server answers the tag instead (it refused the literal), returns that result.</summary>
        private void WaitForContinuation(string tag, List<List<object?>> untagged, out Result? refused)
        {
            refused = null;
            while (true)
            {
                List<object?> response = ImapParser.ReadResponse(Connection.Reader);
                string? first = ImapParser.AsString(response.FirstOrDefault());

                if (first == "+")
                    return;
                if (first == "*")
                {
                    untagged.Add(response);
                    continue;
                }
                if (first == tag)
                {
                    refused = Tagged(response, untagged);
                    return;
                }

                throw new ProtocolViolationException($"Unexpected IMAP response '{first}' while waiting for a continuation.");
            }
        }

        private Result ReadUntilTagged(string tag, List<List<object?>> untagged)
        {
            // Untagged data (including BYE before LOGOUT completes) is collected until our tag answers.
            while (true)
            {
                List<object?> response = ImapParser.ReadResponse(Connection.Reader);
                string? first = ImapParser.AsString(response.FirstOrDefault());

                if (first == "*")
                    untagged.Add(response);
                else if (first == tag)
                    return Tagged(response, untagged);
                else if (first != "+")
                    throw new ProtocolViolationException($"Unexpected IMAP response tag '{first}'.");
            }
        }

        private Result Tagged(List<object?> response, List<List<object?>> untagged)
        {
            string status = ImapParser.AsString(response.ElementAtOrDefault(1))?.ToUpperInvariant() ?? "";
            var result = new Result(status, Text(response, 2), untagged);

            // Servers may announce new capabilities in the tagged response (after authentication, for example).
            if (result.Ok && result.Text.StartsWith("[CAPABILITY ", StringComparison.OrdinalIgnoreCase))
                _capabilities = new(result.Text[12..result.Text.IndexOf(']')].Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);

            return result;
        }

        private Result Sasl(string mechanism, string initialResponse)
        {
            if (_capabilities.Contains("SASL-IR"))
                return Execute($"AUTHENTICATE {mechanism} {initialResponse}");

            string tag = "A" + (++_tag).ToString("D4", CultureInfo.InvariantCulture);
            var untagged = new List<List<object?>>();
            Connection.WriteLine($"{tag} AUTHENTICATE {mechanism}");
            Connection.Flush();

            WaitForContinuation(tag, untagged, out Result? refused);
            if (refused is not null)
                return refused;

            Connection.WriteLine(initialResponse);
            Connection.Flush();

            // XOAUTH2 answers a failure with a "+ <error>" challenge that must be acknowledged with an empty line.
            while (true)
            {
                List<object?> response = ImapParser.ReadResponse(Connection.Reader);
                string? first = ImapParser.AsString(response.FirstOrDefault());
                if (first == "+")
                {
                    Connection.WriteLine("");
                    Connection.Flush();
                    continue;
                }
                if (first == "*")
                {
                    untagged.Add(response);
                    continue;
                }
                return Tagged(response, untagged);
            }
        }

        private void FinishAuthentication(Result result)
        {
            Expect(result, "Authentication failed.");
            IsAuthenticated = true;

            // Capabilities often change after authentication; refresh unless the server already sent them.
            if (!result.Text.StartsWith("[CAPABILITY ", StringComparison.OrdinalIgnoreCase))
                LoadCapabilities();
        }

        private void LoadCapabilities()
        {
            Result result = Expect(Execute("CAPABILITY"), "CAPABILITY failed.");
            _capabilities = new(result.Untagged
                .Where(r => IsKeyword(r, 1, "CAPABILITY"))
                .SelectMany(r => r.Skip(2))
                .Select(ImapParser.AsString)
                .OfType<string>(), StringComparer.OrdinalIgnoreCase);
        }

        private void Store(IEnumerable<uint> uids, string operation, string[] flags)
        {
            RequireSelected();
            string set = UidSet(uids);
            if (set.Length > 0)
                Expect(Execute($"UID STORE {set} {operation} ({string.Join(' ', flags)})"), "STORE failed.");
        }

        private void RequireSelected()
        {
            if (SelectedFolder is null)
                throw new InvalidOperationException("Select a folder first.");
        }

        private static IEnumerable<Dictionary<string, object?>> FetchItems(Result result)
        {
            foreach (List<object?> response in result.Untagged.Where(r => IsKeyword(r, 2, "FETCH")))
            {
                var list = ImapParser.AsList(response.ElementAtOrDefault(3));
                var items = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i + 1 < list.Count; i += 2)
                {
                    if (ImapParser.AsString(list[i]) is { } key)
                        items[key.ToUpperInvariant()] = list[i + 1];
                }
                yield return items;
            }
        }

        private static (string Name, string Arguments)? ResponseCode(List<object?> response)
        {
            if (ImapParser.AsString(response.ElementAtOrDefault(2)) is not { } code || !code.StartsWith('[') || !code.EndsWith(']'))
                return null;

            string[] parts = code[1..^1].Split(' ', 2);
            return (parts[0].ToUpperInvariant(), parts.Length > 1 ? parts[1] : "");
        }

        private static string UidSet(IEnumerable<uint> uids)
        {
            // Compress consecutive runs: 1,2,3,7 -> 1:3,7
            var sorted = uids.Distinct().Order().ToList();
            var ranges = new List<string>();
            for (int i = 0; i < sorted.Count;)
            {
                int j = i;
                while (j + 1 < sorted.Count && sorted[j + 1] == sorted[j] + 1)
                    j++;
                ranges.Add(i == j ? sorted[i].ToString(CultureInfo.InvariantCulture) : $"{sorted[i]}:{sorted[j]}");
                i = j + 1;
            }
            return string.Join(',', ranges);
        }

        private static bool IsAtom(string value)
            => value.Length > 0 && value.All(c => c is > ' ' and < (char)127 && !"(){ %*\"\\]".Contains(c));

        private static bool IsKeyword(List<object?> response, int index, string keyword)
            => string.Equals(ImapParser.AsString(response.ElementAtOrDefault(index)), keyword, StringComparison.OrdinalIgnoreCase);

        private static int ParseInt(object? value)
            => int.TryParse(ImapParser.AsString(value), NumberStyles.None, CultureInfo.InvariantCulture, out int number) ? number : 0;

        private static string Text(List<object?> response, int from)
            => string.Join(' ', response.Skip(from).Select(v => v switch { List<object?> list => "(" + string.Join(' ', list.Select(ImapParser.AsString)) + ")", _ => ImapParser.AsString(v) }));

        private static DateTimeOffset? ParseInternalDate(string? text)
        {
            // "17-Jul-1996 02:44:25 -0700" (the day may be space-padded): add the colon .NET expects in the offset.
            if (text is null || text.Trim() is not { Length: > 5 } trimmed)
                return null;

            string normalized = trimmed.Insert(trimmed.Length - 2, ":");
            return DateTimeOffset.TryParseExact(normalized, "d-MMM-yyyy HH:mm:ss zzz", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset date)
                ? date
                : null;
        }

        private static string FormatInternalDate(DateTimeOffset date)
        {
            string offset = date.ToString("zzz", CultureInfo.InvariantCulture).Replace(":", "");
            return date.ToString("dd-MMM-yyyy HH:mm:ss ", CultureInfo.InvariantCulture) + offset;
        }

        private static Result Expect(Result result, string message)
            => result.Ok ? result : throw new ImapException(message, result.Status, result.Text);

        private sealed record Result(string Status, string Text, List<List<object?>> Untagged)
        {
            public bool Ok => Status == "OK";
        }
    }
}

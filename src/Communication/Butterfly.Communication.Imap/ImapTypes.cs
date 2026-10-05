using System.Globalization;
using System.Text;

using Butterfly.Communication.Mail;

namespace Butterfly.Communication.Imap
{
    public sealed record ImapFolder(string Name, char? Delimiter, IReadOnlyList<string> Attributes)
    {
        /// <summary>False for \Noselect containers that only hold other folders.</summary>
        public bool CanSelect => !Attributes.Any(a => a.Equals(@"\Noselect", StringComparison.OrdinalIgnoreCase) || a.Equals(@"\NonExistent", StringComparison.OrdinalIgnoreCase));

        /// <summary>The RFC 6154 special use (\Sent, \Trash, \Drafts, \Junk, \Archive, \All, \Flagged), if any.</summary>
        public string? SpecialUse => Attributes.FirstOrDefault(a => a is @"\Sent" or @"\Trash" or @"\Drafts" or @"\Junk" or @"\Archive" or @"\All" or @"\Flagged");
    }

    public sealed record ImapMailboxStatus(string Name, int Exists, int Recent, uint UidValidity, uint? UidNext,
        IReadOnlyList<string> Flags, IReadOnlyList<string> PermanentFlags, bool IsReadOnly);

    public sealed class ImapMessageSummary
    {
        public uint Uid { get; init; }
        public IReadOnlyList<string> Flags { get; init; } = [];
        public long Size { get; init; }
        public DateTimeOffset? InternalDate { get; init; }

        /// <summary>The message parsed from its headers only (no body).</summary>
        public MailMessage? Headers { get; init; }

        public bool IsSeen => Flags.Contains(ImapFlags.Seen, StringComparer.OrdinalIgnoreCase);
    }

    public static class ImapFlags
    {
        public const string Seen = @"\Seen";
        public const string Answered = @"\Answered";
        public const string Flagged = @"\Flagged";
        public const string Deleted = @"\Deleted";
        public const string Draft = @"\Draft";
    }

    public class ImapException : CommunicationException
    {
        public ImapException(string message, string? status = null, string? serverMessage = null)
            : base(serverMessage is null ? message : $"{message} Server said: {status} {serverMessage}")
        {
            Status = status;
            ServerMessage = serverMessage;
        }

        /// <summary>"NO" (operational failure) or "BAD" (protocol error).</summary>
        public string? Status { get; }
        public string? ServerMessage { get; }
    }

    /// <summary>
    /// SEARCH criteria built safely (strings are quoted or sent as literals). Combine with <see cref="And"/>,
    /// <see cref="Or"/> and <see cref="Not"/>: <c>ImapQuery.Unseen.And(ImapQuery.From("jefe@example.com"))</c>.
    /// </summary>
    public sealed class ImapQuery
    {
        internal ImapQuery(IReadOnlyList<object> parts) => Parts = parts;

        /// <summary>Text segments (string) and values (<see cref="ImapString"/>) in order.</summary>
        internal IReadOnlyList<object> Parts { get; }

        internal bool NeedsUtf8 => Parts.OfType<ImapString>().Any(s => !MimeEncoding.IsAscii(s.Value));

        public static ImapQuery All { get; } = Keyword("ALL");
        public static ImapQuery Seen { get; } = Keyword("SEEN");
        public static ImapQuery Unseen { get; } = Keyword("UNSEEN");
        public static ImapQuery Flagged { get; } = Keyword("FLAGGED");
        public static ImapQuery Answered { get; } = Keyword("ANSWERED");
        public static ImapQuery Deleted { get; } = Keyword("DELETED");

        public static ImapQuery From(string text) => WithString("FROM", text);
        public static ImapQuery To(string text) => WithString("TO", text);
        public static ImapQuery Subject(string text) => WithString("SUBJECT", text);
        public static ImapQuery Body(string text) => WithString("BODY", text);
        public static ImapQuery Text(string text) => WithString("TEXT", text);
        public static ImapQuery Header(string field, string text) => new(["HEADER ", new ImapString(field), " ", new ImapString(text)]);

        /// <summary>Messages whose internal date is on or after <paramref name="date"/>.</summary>
        public static ImapQuery Since(DateOnly date) => Keyword("SINCE " + FormatDate(date));
        public static ImapQuery Before(DateOnly date) => Keyword("BEFORE " + FormatDate(date));
        public static ImapQuery LargerThan(long bytes) => Keyword("LARGER " + bytes.ToString(CultureInfo.InvariantCulture));
        public static ImapQuery SmallerThan(long bytes) => Keyword("SMALLER " + bytes.ToString(CultureInfo.InvariantCulture));

        public ImapQuery And(ImapQuery other) => new([.. Parts, " ", .. other.Parts]);
        public ImapQuery Or(ImapQuery other) => new(["OR (", .. Parts, ") (", .. other.Parts, ")"]);
        public static ImapQuery Not(ImapQuery query) => new(["NOT (", .. query.Parts, ")"]);

        private static ImapQuery Keyword(string keyword) => new([keyword]);
        private static ImapQuery WithString(string key, string value) => new([key + " ", new ImapString(value)]);

        internal static string FormatDate(DateOnly date) => date.ToString("d-MMM-yyyy", CultureInfo.InvariantCulture);
    }

    /// <summary>A string argument: sent as an atom, a quoted string or a literal, whichever is valid.</summary>
    internal sealed record ImapString(string Value)
    {
        public byte[] Utf8 => Encoding.UTF8.GetBytes(Value);
    }

    /// <summary>A pre-encoded literal payload (APPEND).</summary>
    internal sealed record ImapLiteral(byte[] Data);
}

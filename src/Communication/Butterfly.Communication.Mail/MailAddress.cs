using System.Collections.ObjectModel;
using System.Text;

namespace Butterfly.Communication.Mail
{
    /// <summary>An address such as <c>"Ana Pérez" &lt;ana@example.com&gt;</c>.</summary>
    public sealed class MailAddress : IEquatable<MailAddress>
    {
        private const string Specials = "()<>[]:;@\\,.\"";

        public MailAddress(string address, string? displayName = null)
        {
            ArgumentNullException.ThrowIfNull(address);
            address = address.Trim();

            int at = address.LastIndexOf('@');
            if (at <= 0 || at == address.Length - 1 || address.Any(c => char.IsWhiteSpace(c) || c is '<' or '>' or ',' or '\0'))
                throw new FormatException($"'{address}' is not a valid e-mail address.");

            Address = address;
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
        }

        public string Address { get; }
        public string? DisplayName { get; }
        public string LocalPart => Address[..Address.LastIndexOf('@')];
        public string Domain => Address[(Address.LastIndexOf('@') + 1)..];

        /// <summary>True when the address itself (not the display name) needs SMTPUTF8 (RFC 6531).</summary>
        public bool IsInternational => !MimeEncoding.IsAscii(Address);

        public static implicit operator MailAddress(string address) => Parse(address);

        /// <summary>Parses "a@b", "&lt;a@b&gt;", "Name &lt;a@b&gt;", "\"Last, First\" &lt;a@b&gt;" or "a@b (Name)".</summary>
        public static MailAddress Parse(string text)
            => TryParse(text, out MailAddress? address) ? address : throw new FormatException($"'{text}' is not a valid e-mail address.");

        public static bool TryParse(string? text, out MailAddress address)
        {
            address = null!;
            if (string.IsNullOrWhiteSpace(text))
                return false;

            text = text.Trim();
            string? displayName = null;
            string addressPart;

            int open = LastUnquotedIndexOf(text, '<');
            if (open >= 0)
            {
                int close = text.IndexOf('>', open);
                if (close < 0)
                    return false;

                addressPart = text[(open + 1)..close];
                displayName = MimeEncoding.DecodeHeaderText(ParameterizedValue.Unquote(text[..open].Trim()));
            }
            else
            {
                // "a@b (Name)": the comment is the old way of giving a display name.
                int comment = text.IndexOf('(');
                if (comment > 0 && text.EndsWith(')'))
                {
                    displayName = MimeEncoding.DecodeHeaderText(text[(comment + 1)..^1].Trim());
                    addressPart = text[..comment];
                }
                else
                {
                    addressPart = text;
                }
            }

            try
            {
                address = new MailAddress(addressPart, displayName);
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        /// <summary>Parses a comma-separated list; RFC 5322 groups ("Team: a@b, c@d;") are flattened.</summary>
        public static IReadOnlyList<MailAddress> ParseList(string? text)
        {
            var addresses = new List<MailAddress>();
            if (string.IsNullOrWhiteSpace(text))
                return addresses;

            foreach (string item in SplitList(text))
            {
                string entry = item.Trim();

                // Group syntax: "display-name:" before the members, ";" after them.
                int colon = UnquotedIndexOf(entry, ':');
                if (colon >= 0 && UnquotedIndexOf(entry, '<') is var angle && (angle < 0 || colon < angle))
                    entry = entry[(colon + 1)..];
                entry = entry.TrimEnd(';').Trim();

                if (TryParse(entry, out MailAddress? address))
                    addresses.Add(address);
            }

            return addresses;
        }

        public override string ToString() => DisplayName is null ? Address : $"{QuoteIfNeeded(DisplayName)} <{Address}>";

        /// <summary>The form used in a header: the display name RFC 2047 encoded when it is not ASCII.</summary>
        public string ToHeaderString()
        {
            if (DisplayName is null)
                return Address;

            string name = MimeEncoding.IsAscii(DisplayName) ? QuoteIfNeeded(DisplayName) : MimeEncoding.EncodeHeaderText(DisplayName);
            return $"{name} <{Address}>";
        }

        public bool Equals(MailAddress? other) => other is not null && Address.Equals(other.Address, StringComparison.OrdinalIgnoreCase);

        public override bool Equals(object? obj) => Equals(obj as MailAddress);

        public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Address);

        private static string QuoteIfNeeded(string name)
            => name.Any(c => Specials.Contains(c)) ? "\"" + name.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"" : name;

        private static IEnumerable<string> SplitList(string text)
        {
            var current = new StringBuilder();
            bool quoted = false;
            int angle = 0, comment = 0;

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\\' && quoted && i + 1 < text.Length)
                {
                    current.Append(c).Append(text[++i]);
                    continue;
                }

                switch (c)
                {
                    case '"': quoted = !quoted; break;
                    case '<' when !quoted: angle++; break;
                    case '>' when !quoted: angle = Math.Max(0, angle - 1); break;
                    case '(' when !quoted: comment++; break;
                    case ')' when !quoted: comment = Math.Max(0, comment - 1); break;
                    case ',' or ';' when !quoted && angle == 0 && comment == 0:
                        if (c == ';')
                            current.Append(c);
                        yield return current.ToString();
                        current.Clear();
                        continue;
                }

                current.Append(c);
            }

            if (current.Length > 0)
                yield return current.ToString();
        }

        private static int UnquotedIndexOf(string text, char target)
        {
            bool quoted = false;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '"')
                    quoted = !quoted;
                else if (text[i] == target && !quoted)
                    return i;
            }
            return -1;
        }

        private static int LastUnquotedIndexOf(string text, char target)
        {
            bool quoted = false;
            int found = -1;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '"')
                    quoted = !quoted;
                else if (text[i] == target && !quoted)
                    found = i;
            }
            return found;
        }
    }

    public sealed class MailAddressCollection : Collection<MailAddress>
    {
        /// <summary>Adds one address or a comma-separated list.</summary>
        public void Add(string addresses)
        {
            foreach (MailAddress address in MailAddress.ParseList(addresses))
                Add(address);
        }

        public string ToHeaderString() => string.Join(", ", this.Select(a => a.ToHeaderString()));

        public override string ToString() => string.Join(", ", this);
    }
}

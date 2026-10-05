using System.Collections;
using System.Globalization;
using System.Text;

namespace Butterfly.Communication.Mail
{
    /// <summary>Ordered, case-insensitive MIME header list. Values are stored decoded-as-received (not RFC 2047 decoded).</summary>
    public sealed class MimeHeaders : IEnumerable<KeyValuePair<string, string>>
    {
        private readonly List<KeyValuePair<string, string>> _items = [];

        public int Count => _items.Count;

        public string? this[string name]
        {
            get => Get(name);
            set
            {
                Remove(name);
                if (value is not null)
                    Add(name, value);
            }
        }

        public void Add(string name, string value)
        {
            if (name.Length == 0 || name.Any(c => c is <= ' ' or >= (char)127 or ':'))
                throw new ArgumentException($"'{name}' is not a valid header name.", nameof(name));
            if (value.AsSpan().IndexOfAny('\r', '\n') >= 0)
                throw new ArgumentException($"The value of header '{name}' contains a line break.", nameof(value));

            _items.Add(new(name, value));
        }

        public bool Remove(string name) => _items.RemoveAll(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)) > 0;

        /// <summary>The first value of the header.</summary>
        public string? Get(string name) => _items.FirstOrDefault(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

        public IReadOnlyList<string> GetValues(string name)
            => [.. _items.Where(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(h => h.Value)];

        public bool Contains(string name) => _items.Exists(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase));

        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _items.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        internal void AddRaw(string name, string value) => _items.Add(new(name, value));
    }

    /// <summary>A header value with parameters, such as Content-Type or Content-Disposition.</summary>
    public class ParameterizedValue
    {
        protected ParameterizedValue(string value, IReadOnlyDictionary<string, string> parameters)
        {
            Value = value;
            Parameters = parameters;
        }

        public string Value { get; }

        /// <summary>Parameters with RFC 2231 continuations and charsets already decoded; names are case-insensitive.</summary>
        public IReadOnlyDictionary<string, string> Parameters { get; }

        public string? Parameter(string name) => Parameters.GetValueOrDefault(name);

        internal static (string Value, Dictionary<string, string> Parameters) ParseValue(string header)
        {
            var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var parts = SplitParameters(header);
            string value = parts.Count > 0 ? parts[0].Trim() : "";

            // RFC 2231: name*=charset'lang'%XX and continuations name*0, name*1*, ...
            var continuations = new SortedDictionary<(string Name, int Index), (string Value, bool Encoded)>();
            foreach (string part in parts.Skip(1))
            {
                int equals = part.IndexOf('=');
                if (equals <= 0)
                    continue;

                string name = part[..equals].Trim();
                string raw = Unquote(part[(equals + 1)..].Trim());
                bool encoded = name.EndsWith('*');
                if (encoded)
                    name = name[..^1];

                int star = name.IndexOf('*');
                if (star > 0 && int.TryParse(name.AsSpan(star + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int index))
                    continuations[(name[..star].ToLowerInvariant(), index)] = (raw, encoded);
                else
                    parameters[name] = encoded ? DecodeRfc2231(raw, out _) : MimeEncoding.DecodeHeaderText(raw);
            }

            foreach (var group in continuations.GroupBy(c => c.Key.Name))
            {
                Encoding? encoding = null;
                var bytes = new List<byte>();
                foreach (var ((_, index), (raw, encoded)) in group)
                {
                    if (!encoded)
                    {
                        bytes.AddRange(Encoding.UTF8.GetBytes(raw));
                        continue;
                    }

                    string data = raw;
                    if (index == 0 && raw.Count(c => c == '\'') >= 2)
                    {
                        int first = raw.IndexOf('\''), second = raw.IndexOf('\'', first + 1);
                        encoding = Charsets.GetEncoding(raw[..first]);
                        data = raw[(second + 1)..];
                    }
                    bytes.AddRange(PercentDecode(data));
                }
                parameters[group.Key] = (encoding ?? Encoding.UTF8).GetString([.. bytes]);
            }

            return (value, parameters);
        }

        private static string DecodeRfc2231(string raw, out Encoding encoding)
        {
            encoding = Encoding.UTF8;
            int first = raw.IndexOf('\'');
            int second = first < 0 ? -1 : raw.IndexOf('\'', first + 1);
            if (second > 0)
            {
                encoding = Charsets.GetEncoding(raw[..first]);
                raw = raw[(second + 1)..];
            }
            return encoding.GetString(PercentDecode(raw));
        }

        private static byte[] PercentDecode(string text)
        {
            var bytes = new List<byte>(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '%' && i + 2 < text.Length && char.IsAsciiHexDigit(text[i + 1]) && char.IsAsciiHexDigit(text[i + 2]))
                {
                    bytes.Add(byte.Parse(text.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    i += 2;
                }
                else
                {
                    bytes.AddRange(Encoding.UTF8.GetBytes(text[i].ToString()));
                }
            }
            return [.. bytes];
        }

        private static List<string> SplitParameters(string header)
        {
            var parts = new List<string>();
            var current = new StringBuilder();
            bool quoted = false;

            for (int i = 0; i < header.Length; i++)
            {
                char c = header[i];
                if (c == '\\' && quoted && i + 1 < header.Length)
                {
                    current.Append(c).Append(header[++i]);
                    continue;
                }
                if (c == '"')
                    quoted = !quoted;
                if (c == ';' && !quoted)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                    continue;
                }
                current.Append(c);
            }

            parts.Add(current.ToString());
            return parts;
        }

        internal static string Unquote(string value)
        {
            if (value.Length < 2 || value[0] != '"' || value[^1] != '"')
                return value;

            var builder = new StringBuilder(value.Length);
            for (int i = 1; i < value.Length - 1; i++)
            {
                if (value[i] == '\\' && i + 1 < value.Length - 1)
                    i++;
                builder.Append(value[i]);
            }
            return builder.ToString();
        }
    }

    public sealed class ContentType : ParameterizedValue
    {
        private ContentType(string value, IReadOnlyDictionary<string, string> parameters) : base(value.ToLowerInvariant(), parameters) { }

        /// <summary>RFC 2045 default for entities without a Content-Type.</summary>
        public static ContentType Default { get; } = Parse("text/plain; charset=us-ascii");

        /// <summary>"text/plain", "multipart/mixed"...</summary>
        public string MediaType => Value;

        public string TopLevelType => MediaType.Split('/')[0];

        public bool IsMultipart => TopLevelType == "multipart";

        public string? Charset => Parameter("charset");

        public string? Boundary => Parameter("boundary");

        public string? Name => Parameter("name");

        public bool Is(string mediaType) => MediaType.Equals(mediaType, StringComparison.OrdinalIgnoreCase);

        public static ContentType Parse(string header)
        {
            var (value, parameters) = ParseValue(header);
            return new ContentType(value.Contains('/') ? value : "application/octet-stream", parameters);
        }

        public override string ToString() => MediaType;
    }

    public sealed class ContentDisposition : ParameterizedValue
    {
        private ContentDisposition(string value, IReadOnlyDictionary<string, string> parameters) : base(value.ToLowerInvariant(), parameters) { }

        public bool IsAttachment => Value == "attachment";

        public bool IsInline => Value == "inline";

        public string? FileName => Parameter("filename");

        public static ContentDisposition Parse(string header)
        {
            var (value, parameters) = ParseValue(header);
            return new ContentDisposition(value, parameters);
        }

        public override string ToString() => Value;
    }
}

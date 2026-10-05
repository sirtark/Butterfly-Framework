using System.Collections;
using System.Globalization;

namespace Butterfly.Communication.Http
{
    /// <summary>
    /// Ordered, case-insensitive header list that allows repeated names (Set-Cookie). Names and values are
    /// validated so a value can never inject extra header lines.
    /// </summary>
    public sealed class HttpHeaders : IEnumerable<KeyValuePair<string, string>>
    {
        private readonly List<KeyValuePair<string, string>> _items = [];

        public int Count => _items.Count;

        /// <summary>Gets the (comma-joined) value, or sets it replacing previous ones; null removes the header.</summary>
        public string? this[string name]
        {
            get => Get(name);
            set
            {
                if (value is null)
                    Remove(name);
                else
                    Set(name, value);
            }
        }

        public HttpHeaders Add(string name, string value)
        {
            Validate(name, value);
            _items.Add(new(name, value.Trim()));
            return this;
        }

        public HttpHeaders Set(string name, string value)
        {
            Validate(name, value);
            Remove(name);
            _items.Add(new(name, value.Trim()));
            return this;
        }

        public bool Remove(string name) => _items.RemoveAll(h => Matches(h.Key, name)) > 0;

        public bool Contains(string name) => _items.Exists(h => Matches(h.Key, name));

        /// <summary>All values joined with ", " as RFC 9110 allows (Set-Cookie is the exception: use <see cref="GetValues"/>).</summary>
        public string? Get(string name)
        {
            IReadOnlyList<string> values = GetValues(name);
            return values.Count switch
            {
                0 => null,
                1 => values[0],
                _ => string.Join(", ", values)
            };
        }

        public IReadOnlyList<string> GetValues(string name) => [.. _items.Where(h => Matches(h.Key, name)).Select(h => h.Value)];

        /// <summary>True when a comma-separated header (Connection, Transfer-Encoding...) contains <paramref name="token"/>.</summary>
        public bool HasToken(string name, string token)
            => GetValues(name).SelectMany(v => v.Split(',')).Any(t => t.Trim().Equals(token, StringComparison.OrdinalIgnoreCase));

        public long? ContentLength
            => long.TryParse(Get("Content-Length"), NumberStyles.None, CultureInfo.InvariantCulture, out long length) ? length : null;

        public string? ContentType => Get("Content-Type");

        public void AddRange(HttpHeaders headers)
        {
            foreach (var header in headers._items)
                _items.Add(header);
        }

        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _items.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public override string ToString() => string.Join("\r\n", _items.Select(h => $"{h.Key}: {h.Value}"));

        internal void AddUnvalidated(string name, string value) => _items.Add(new(name, value));

        internal void AppendToLast(string continuation)
        {
            if (_items.Count > 0)
                _items[^1] = new(_items[^1].Key, _items[^1].Value + " " + continuation.Trim());
        }

        private static bool Matches(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);

        private static void Validate(string name, string value)
        {
            ArgumentNullException.ThrowIfNull(name);
            ArgumentNullException.ThrowIfNull(value);

            if (!IsToken(name))
                throw new ArgumentException($"'{name}' is not a valid header name.", nameof(name));

            if (value.AsSpan().IndexOfAny('\r', '\n', '\0') >= 0)
                throw new ArgumentException($"The value of header '{name}' contains a line break.", nameof(value));
        }

        /// <summary>RFC 9110 token: letters, digits and !#$%&amp;'*+-.^_`|~.</summary>
        public static bool IsToken(string value)
        {
            if (value.Length == 0)
                return false;

            foreach (char c in value)
            {
                if (!(char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c)))
                    return false;
            }

            return true;
        }
    }
}

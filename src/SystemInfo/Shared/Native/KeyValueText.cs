namespace Butterfly.SystemInfo.Native
{
    // "key<separator>value" lines: /proc/cpuinfo, /proc/meminfo (':') and /etc/os-release ('=').
    internal static class KeyValueText
    {
        public static Dictionary<string, string> Parse(string? text, char separator)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (text is null)
                return values;

            foreach (var line in text.AsSpan().EnumerateLines())
            {
                var index = line.IndexOf(separator);
                if (index <= 0)
                    continue;

                var key = line[..index].Trim();
                if (key.IsEmpty)
                    continue;

                // First occurrence wins: /proc/cpuinfo repeats the same keys once per processor.
                values.TryAdd(key.ToString(), Unquote(line[(index + 1)..].Trim()).ToString());
            }
            return values;
        }

        private static ReadOnlySpan<char> Unquote(ReadOnlySpan<char> value) =>
            value.Length >= 2 && value[0] is '"' or '\'' && value[^1] == value[0] ? value[1..^1] : value;
    }
}

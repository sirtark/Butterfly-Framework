using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Butterfly.Communication.Mail
{
    /// <summary>The encodings of Internet mail: RFC 2047 encoded words, quoted-printable, base64 lines, header folding.</summary>
    public static partial class MimeEncoding
    {
        public const int MaxLineLength = 76;

        // =?charset?B|Q?text?=   (the language suffix of RFC 2231, "charset*lang", is accepted and ignored)
        [GeneratedRegex(@"=\?([^?\s]+)\?([BbQq])\?([^?\s]*)\?=", RegexOptions.CultureInvariant)]
        private static partial Regex EncodedWord();

        [GeneratedRegex(@"(=\?[^?\s]+\?[BbQq]\?[^?\s]*\?=)\s+(?==\?)", RegexOptions.CultureInvariant)]
        private static partial Regex SpaceBetweenEncodedWords();

        public static bool IsAscii(string text) => Ascii.IsValid(text);

        /// <summary>Encodes non-ASCII text as UTF-8 "B" encoded words (at most 75 characters each).</summary>
        public static string EncodeHeaderText(string text)
        {
            if (IsAscii(text))
                return text;

            var words = new List<string>();
            var chunk = new StringBuilder();
            int chunkBytes = 0;

            // 45 bytes of UTF-8 become 60 base64 characters: with "=?utf-8?B?" and "?=" the word stays under 75.
            foreach (Rune rune in text.EnumerateRunes())
            {
                int size = rune.Utf8SequenceLength;
                if (chunkBytes + size > 45)
                {
                    words.Add(Word(chunk.ToString()));
                    chunk.Clear();
                    chunkBytes = 0;
                }
                chunk.Append(rune.ToString());
                chunkBytes += size;
            }

            if (chunk.Length > 0)
                words.Add(Word(chunk.ToString()));

            return string.Join(' ', words);

            static string Word(string value) => $"=?utf-8?B?{Convert.ToBase64String(Encoding.UTF8.GetBytes(value))}?=";
        }

        /// <summary>Decodes RFC 2047 encoded words (whitespace between adjacent words is dropped, as the RFC requires).</summary>
        public static string DecodeHeaderText(string text)
        {
            if (!text.Contains("=?", StringComparison.Ordinal))
                return text;

            text = SpaceBetweenEncodedWords().Replace(text, "$1");
            return EncodedWord().Replace(text, match =>
            {
                string charset = match.Groups[1].Value.Split('*')[0];
                string payload = match.Groups[3].Value;
                try
                {
                    byte[] bytes = match.Groups[2].Value is "B" or "b"
                        ? Convert.FromBase64String(PadBase64(payload))
                        : DecodeQuotedPrintable(payload.Replace('_', ' '), headerMode: true);
                    return Charsets.GetEncoding(charset).GetString(bytes);
                }
                catch (FormatException)
                {
                    return match.Value;
                }
            });
        }

        /// <summary>Quoted-printable (RFC 2045 6.7). CRLF in the input stays a hard line break.</summary>
        public static string EncodeQuotedPrintable(ReadOnlySpan<byte> data)
        {
            var output = new StringBuilder(data.Length * 3 / 2);
            int lineLength = 0;

            for (int i = 0; i < data.Length; i++)
            {
                byte b = data[i];

                if (b == '\r' && i + 1 < data.Length && data[i + 1] == '\n')
                {
                    output.Append("\r\n");
                    lineLength = 0;
                    i++;
                    continue;
                }

                // Whitespace at the end of a line would be stripped by transport: encode it.
                bool lineEnd = i + 1 == data.Length || (data[i + 1] == '\r' && i + 2 < data.Length && data[i + 2] == '\n');
                bool literal = (b is >= 33 and <= 126 && b != '=') || (b is (byte)' ' or (byte)'\t' && !lineEnd);
                string token = literal ? ((char)b).ToString() : "=" + b.ToString("X2", CultureInfo.InvariantCulture);

                if (lineLength + token.Length > MaxLineLength - 1)
                {
                    output.Append("=\r\n");
                    lineLength = 0;
                }

                output.Append(token);
                lineLength += token.Length;
            }

            return output.ToString();
        }

        public static byte[] DecodeQuotedPrintable(string text) => DecodeQuotedPrintable(text, headerMode: false);

        private static byte[] DecodeQuotedPrintable(string text, bool headerMode)
        {
            var output = new List<byte>(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c != '=')
                {
                    // Anything outside Latin-1 in a QP body is already invalid: keep its UTF-8 bytes.
                    if (c < 256)
                        output.Add((byte)c);
                    else
                        output.AddRange(Encoding.UTF8.GetBytes(c.ToString()));
                    continue;
                }

                // Soft line break: "=" followed by CRLF (or LF, or trailing whitespace then CRLF).
                int j = i + 1;
                while (!headerMode && j < text.Length && text[j] is ' ' or '\t')
                    j++;
                if (!headerMode && j < text.Length && text[j] is '\r' or '\n')
                {
                    i = text[j] == '\r' && j + 1 < text.Length && text[j + 1] == '\n' ? j + 1 : j;
                    continue;
                }

                if (i + 2 < text.Length && IsHex(text[i + 1]) && IsHex(text[i + 2]))
                {
                    output.Add(byte.Parse(text.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    i += 2;
                    continue;
                }

                if (i + 1 == text.Length && !headerMode)
                    continue; // soft break at the very end

                output.Add((byte)'='); // invalid sequence: keep it literally (RFC 2045 recommends leniency)
            }

            return [.. output];
        }

        /// <summary>Base64 wrapped in lines of 76 characters.</summary>
        public static string EncodeBase64Lines(ReadOnlySpan<byte> data)
        {
            string base64 = Convert.ToBase64String(data);
            var output = new StringBuilder(base64.Length + base64.Length / MaxLineLength * 2 + 2);
            for (int i = 0; i < base64.Length; i += MaxLineLength)
                output.Append(base64, i, Math.Min(MaxLineLength, base64.Length - i)).Append("\r\n");
            return output.ToString();
        }

        /// <summary>Decodes base64 ignoring line breaks and other characters outside the alphabet (common in the wild).</summary>
        public static byte[] DecodeBase64(string text)
        {
            var clean = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                if (char.IsAsciiLetterOrDigit(c) || c is '+' or '/')
                    clean.Append(c);
            }

            string base64 = PadBase64(clean.ToString());
            if (base64.Length % 4 == 1)
                base64 = base64[..^1];

            return Convert.FromBase64String(base64);
        }

        /// <summary>
        /// Formats "Name: value" folded at whitespace (or after commas for address lists) so no line exceeds 78 characters.
        /// </summary>
        public static string FoldHeader(string name, string value)
        {
            string line = $"{name}: {value}";
            if (line.Length <= 78)
                return line;

            var output = new StringBuilder();
            int lineStart = 0;
            int lastBreak = -1;

            for (int i = 0; i < line.Length; i++)
            {
                if (line[i] == ' ' && i > name.Length + 1)
                    lastBreak = i;

                if (i - lineStart >= 78 && lastBreak > lineStart)
                {
                    output.Append(line, lineStart, lastBreak - lineStart).Append("\r\n");
                    lineStart = lastBreak; // the space starts the continuation line
                    lastBreak = -1;
                }
            }

            output.Append(line, lineStart, line.Length - lineStart);
            return output.ToString();
        }

        /// <summary>A MIME parameter: quoted when needed, RFC 2231 encoded (name*=utf-8''...) when not ASCII.</summary>
        public static string FormatParameter(string name, string value)
        {
            if (!IsAscii(value))
            {
                var encoded = new StringBuilder();
                foreach (byte b in Encoding.UTF8.GetBytes(value))
                {
                    if (char.IsAsciiLetterOrDigit((char)b) || "!#$&+-.^_`|~".Contains((char)b))
                        encoded.Append((char)b);
                    else
                        encoded.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
                }
                return $"{name}*=utf-8''{encoded}";
            }

            return $"{name}=\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
        }

        private static string PadBase64(string value) => (value.Length % 4) switch
        {
            2 => value + "==",
            3 => value + "=",
            _ => value
        };

        private static bool IsHex(char c) => char.IsAsciiHexDigit(c);
    }
}

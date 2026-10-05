using System.Globalization;
using System.Text;

namespace Butterfly.Communication.Imap
{
    /// <summary>
    /// Tokenizes IMAP responses into values: <see cref="string"/> (atoms and quoted strings), <c>byte[]</c> (literals),
    /// <c>null</c> (NIL) and <c>List&lt;object?&gt;</c> (parenthesized lists). Atoms keep their [sections] attached,
    /// so "BODY[HEADER]" and "[UIDVALIDITY 42]" come back as single tokens.
    /// </summary>
    internal static class ImapParser
    {
        /// <summary>Reads one response, following literals ("{n}" at the end of a line) into the next line.</summary>
        public static List<object?> ReadResponse(ProtocolReader reader)
        {
            var segments = new List<object>();
            while (true)
            {
                string line = reader.ReadLine() ?? throw new ImapException("The server closed the connection.");
                segments.Add(line);

                if (!TryGetLiteralLength(line, out int length))
                    break;

                segments.Add(reader.ReadBytes(length));
            }

            return Tokenize(segments);
        }

        public static bool TryGetLiteralLength(string line, out int length)
        {
            length = 0;
            if (!line.EndsWith('}'))
                return false;

            int open = line.LastIndexOf('{');
            ReadOnlySpan<char> digits = line.AsSpan(open + 1, line.Length - open - 2).TrimEnd('+');
            return open >= 0 && int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out length);
        }

        private static List<object?> Tokenize(List<object> segments)
        {
            var root = new List<object?>();
            var stack = new Stack<List<object?>>();
            stack.Push(root);

            for (int s = 0; s < segments.Count; s++)
            {
                if (segments[s] is byte[] literal)
                {
                    stack.Peek().Add(literal);
                    continue;
                }

                string text = (string)segments[s];
                bool literalFollows = s + 1 < segments.Count && segments[s + 1] is byte[];
                if (literalFollows)
                    text = text[..text.LastIndexOf('{')];

                int i = 0;
                while (i < text.Length)
                {
                    char c = text[i];
                    if (c == ' ')
                    {
                        i++;
                    }
                    else if (c == '(')
                    {
                        var list = new List<object?>();
                        stack.Peek().Add(list);
                        stack.Push(list);
                        i++;
                    }
                    else if (c == ')')
                    {
                        if (stack.Count > 1)
                            stack.Pop();
                        i++;
                    }
                    else if (c == '"')
                    {
                        var value = new StringBuilder();
                        for (i++; i < text.Length && text[i] != '"'; i++)
                        {
                            if (text[i] == '\\' && i + 1 < text.Length)
                                i++;
                            value.Append(text[i]);
                        }
                        stack.Peek().Add(value.ToString());
                        i++;
                    }
                    else
                    {
                        int start = i;
                        int depth = 0;
                        while (i < text.Length && (depth > 0 || (text[i] != ' ' && text[i] != '(' && text[i] != ')')))
                        {
                            if (text[i] == '[') depth++;
                            else if (text[i] == ']') depth = Math.Max(0, depth - 1);
                            i++;
                        }

                        string atom = text[start..i];
                        stack.Peek().Add(atom.Equals("NIL", StringComparison.OrdinalIgnoreCase) ? null : atom);
                    }
                }
            }

            return root;
        }

        public static string? AsString(object? value) => value switch
        {
            string text => text,
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            _ => null
        };

        public static List<object?> AsList(object? value) => value as List<object?> ?? [];
    }

    /// <summary>Modified UTF-7 for folder names (RFC 3501 5.1.3).</summary>
    public static class ImapFolderName
    {
        public static string Encode(string name)
        {
            var output = new StringBuilder();
            var pending = new StringBuilder();

            void Flush()
            {
                if (pending.Length == 0)
                    return;
                string base64 = Convert.ToBase64String(Encoding.BigEndianUnicode.GetBytes(pending.ToString())).TrimEnd('=').Replace('/', ',');
                output.Append('&').Append(base64).Append('-');
                pending.Clear();
            }

            foreach (char c in name)
            {
                if (c is >= ' ' and <= '~')
                {
                    Flush();
                    output.Append(c == '&' ? "&-" : c.ToString());
                }
                else
                {
                    pending.Append(c);
                }
            }

            Flush();
            return output.ToString();
        }

        public static string Decode(string encoded)
        {
            var output = new StringBuilder();
            for (int i = 0; i < encoded.Length; i++)
            {
                if (encoded[i] != '&')
                {
                    output.Append(encoded[i]);
                    continue;
                }

                int end = encoded.IndexOf('-', i + 1);
                if (end < 0)
                {
                    output.Append(encoded, i, encoded.Length - i);
                    break;
                }

                if (end == i + 1)
                {
                    output.Append('&');
                }
                else
                {
                    string base64 = encoded[(i + 1)..end].Replace(',', '/');
                    base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
                    try
                    {
                        output.Append(Encoding.BigEndianUnicode.GetString(Convert.FromBase64String(base64)));
                    }
                    catch (FormatException)
                    {
                        output.Append(encoded, i, end - i + 1);
                    }
                }

                i = end;
            }

            return output.ToString();
        }
    }
}

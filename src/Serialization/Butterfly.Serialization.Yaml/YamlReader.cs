using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Butterfly.Serialization.Yaml
{
    // A line-based YAML reader: block structure is decided by indentation, inline content (flow collections, quoted
    // scalars, plain scalars) by a small character scanner.
    internal sealed partial class YamlReader
    {
        private readonly List<string> lines;
        private readonly int maxDepth;
        private readonly Dictionary<string, SerializationValue> anchors = [];
        private int index;

        public YamlReader(string text, int maxDepth)
        {
            lines = [.. text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')];
            this.maxDepth = maxDepth;
        }

        public SerializationValue ReadDocument()
        {
            // Directives and the document start marker.
            while (index < lines.Count && (lines[index].StartsWith('%') || IsBlank(lines[index])))
                index++;
            if (index < lines.Count && (lines[index] == "---" || lines[index].StartsWith("--- ", StringComparison.Ordinal)))
            {
                var rest = lines[index].Length > 4 ? lines[index][4..] : "";
                lines[index] = rest;
                if (IsBlank(rest))
                    index++;
            }

            if (!SkipToContent())
                return SerializationValue.Null;
            var value = ReadNode(-1, 0);

            if (SkipToContent() && lines[index] is not ("..." or "---"))
                throw Error("unexpected content after the document (indentation does not match).");
            return value;
        }

        // ------------------------------------------------------------------ block structure

        private SerializationValue ReadNode(int parentIndent, int depth)
        {
            if (depth > maxDepth)
                throw Error($"nested deeper than {maxDepth} levels.");
            if (!SkipToContent() || Indent(lines[index]) <= parentIndent)
                return SerializationValue.Null;

            var indent = Indent(lines[index]);
            var content = StripComment(lines[index][indent..]);
            if (IsSequenceEntry(content))
                return ReadSequence(indent, depth);
            if (MappingColon(content) >= 0)
                return ReadMapping(indent, depth);

            index++;
            return ReadInline(content, parentIndent, depth);
        }

        private SerializationValue ReadSequence(int indent, int depth)
        {
            var items = new List<SerializationValue>();
            while (SkipToContent() && Indent(lines[index]) == indent)
            {
                var content = StripComment(lines[index][indent..]);
                if (!IsSequenceEntry(content))
                    break;

                var rest = content.Length > 1 ? content[1..].TrimStart(' ') : "";
                var column = indent + (content.Length - rest.Length);
                if (rest.Length == 0)
                {
                    index++;
                    items.Add(ReadNode(indent, depth + 1));
                }
                else if (IsSequenceEntry(rest) || (MappingColon(rest) >= 0 && !StartsInline(rest)))
                {
                    // "- key: value" or "- - x": the rest is a node that starts at its own column.
                    lines[index] = new string(' ', column) + rest;
                    items.Add(ReadNode(indent, depth + 1));
                }
                else
                {
                    index++;
                    items.Add(ReadInline(rest, indent, depth + 1));
                }
            }
            return SerializationValue.Array(items);
        }

        private SerializationValue ReadMapping(int indent, int depth)
        {
            var members = new List<KeyValuePair<string, SerializationValue>>();
            while (SkipToContent() && Indent(lines[index]) == indent)
            {
                var content = StripComment(lines[index][indent..]);
                var colon = MappingColon(content);
                if (colon < 0 || IsSequenceEntry(content))
                    break;

                var key = Key(content[..colon].TrimEnd());
                var rest = content[(colon + 1)..].Trim();
                index++;

                SerializationValue value;
                if (rest.Length == 0)
                {
                    // The value is on the next lines: more indented, or a sequence at the same indentation.
                    if (SkipToContent() && Indent(lines[index]) == indent && IsSequenceEntry(StripComment(lines[index][indent..])))
                        value = ReadSequence(indent, depth + 1);
                    else
                        value = ReadNode(indent, depth + 1);
                }
                else
                {
                    value = ReadInline(rest, indent, depth + 1);
                }
                members.Add(new(key, value));
            }

            if (SkipToContent() && Indent(lines[index]) > indent)
                throw Error("bad indentation.");
            return SerializationValue.Object(members);
        }

        // ------------------------------------------------------------------ inline content

        // The value that starts with `text` (already taken from its line); continuation lines must be indented past parentIndent.
        private SerializationValue ReadInline(string text, int parentIndent, int depth)
        {
            string? anchor = null;
            string? tag = null;
            while (text.Length > 0 && text[0] is '&' or '!')
            {
                var end = text.IndexOf(' ');
                var property = end < 0 ? text : text[..end];
                if (property[0] == '&')
                    anchor = property[1..];
                else
                    tag = property;
                text = end < 0 ? "" : text[(end + 1)..].TrimStart();
            }

            SerializationValue value;
            if (text.Length == 0)
                value = ReadNode(parentIndent, depth + 1);
            else if (text[0] == '*')
                value = anchors.TryGetValue(text[1..].Trim(), out var aliased) ? aliased : throw Error($"unknown alias '{text}'.");
            else if (text[0] is '|' or '>')
                value = SerializationValue.From(BlockScalar(text, parentIndent));
            else if (text[0] is '[' or '{')
                value = new FlowParser(Gather(text, parentIndent), this, depth).Parse();
            else if (text[0] == '"')
                value = SerializationValue.From(DoubleQuoted(GatherQuoted(text, parentIndent, '"')));
            else if (text[0] == '\'')
                value = SerializationValue.From(SingleQuoted(GatherQuoted(text, parentIndent, '\'')));
            else
                value = tag is null ? Resolve(PlainContinuation(text, parentIndent)) : SerializationValue.From(PlainContinuation(text, parentIndent));

            if (tag is not null)
                value = ApplyTag(tag, value);
            if (anchor is not null)
                anchors[anchor] = value;
            return value;
        }

        private SerializationValue ApplyTag(string tag, SerializationValue value)
        {
            var text = value.Kind == ValueKind.String ? value.AsString() : null;
            switch (tag)
            {
                case "!!str" or "!str":
                    return text is not null ? value : SerializationValue.From(value.ToString());
                case "!!binary":
                    try
                    {
                        return SerializationValue.From(Convert.FromBase64String(new string((text ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray())));
                    }
                    catch (FormatException)
                    {
                        throw Error("invalid !!binary data.");
                    }
                case "!!int" or "!!float" or "!!bool" or "!!null":
                    return text is null ? value : Resolve(text);
                case "!!timestamp":
                    return text is not null && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
                        ? SerializationValue.From(date)
                        : throw Error("invalid !!timestamp.");
                default:
                    return value;   // Application tags carry no meaning here.
            }
        }

        private string BlockScalar(string header, int parentIndent)
        {
            var literal = header[0] == '|';
            var chomping = header.Contains('-') ? '-' : header.Contains('+') ? '+' : ' ';
            var explicitIndent = header.FirstOrDefault(char.IsDigit);

            var collected = new List<string>();
            // An explicit indentation indicator counts from the indentation of the parent node.
            var contentIndent = explicitIndent != default ? Math.Max(parentIndent, 0) + (explicitIndent - '0') : -1;

            while (index < lines.Count)
            {
                var line = lines[index];
                if (IsBlank(line))
                {
                    collected.Add("");
                    index++;
                    continue;
                }
                var indent = Indent(line);
                if (contentIndent < 0)
                {
                    if (indent <= parentIndent)
                        break;
                    contentIndent = indent;
                }
                if (indent < contentIndent)
                    break;
                collected.Add(line[contentIndent..]);
                index++;
            }

            // Trailing empty lines belong to chomping.
            var trailing = 0;
            while (collected.Count > 0 && collected[^1].Length == 0)
            {
                collected.RemoveAt(collected.Count - 1);
                trailing++;
            }

            string body;
            if (literal)
            {
                body = string.Join("\n", collected);
            }
            else
            {
                // Folding: line breaks between plain lines become spaces; empty and more-indented lines keep them.
                var builder = new StringBuilder();
                for (var i = 0; i < collected.Count; i++)
                {
                    var line = collected[i];
                    if (i > 0)
                    {
                        var previous = collected[i - 1];
                        var keepBreak = line.Length == 0 || previous.Length == 0 || line.StartsWith(' ') || previous.StartsWith(' ');
                        builder.Append(keepBreak ? "\n" : " ");
                    }
                    builder.Append(line);
                }
                body = Regex.Replace(builder.ToString(), "\n\n", "\n");
            }

            if (collected.Count == 0)
                return chomping == '+' ? new string('\n', trailing) : "";
            return chomping switch
            {
                '-' => body,
                '+' => body + new string('\n', trailing + 1),
                _ => body + "\n"
            };
        }

        // Plain scalars may continue on more indented lines; line breaks fold into spaces.
        private string PlainContinuation(string text, int parentIndent)
        {
            var builder = new StringBuilder(text.TrimEnd());
            while (index < lines.Count && !IsBlank(lines[index]) && Indent(lines[index]) > parentIndent)
            {
                var content = StripComment(lines[index].Trim());
                if (MappingColon(content) >= 0 || IsSequenceEntry(content) || content.Length == 0)
                    break;
                builder.Append(' ').Append(content);
                index++;
            }
            return builder.ToString();
        }

        // Flow collections may span lines: gather until the brackets balance.
        private string Gather(string text, int parentIndent)
        {
            var builder = new StringBuilder(text);
            while (!Balanced(builder.ToString()))
            {
                if (index >= lines.Count)
                    throw Error("unterminated flow collection.");
                builder.Append(' ').Append(StripComment(lines[index].Trim()));
                index++;
            }
            return builder.ToString();
        }

        private string GatherQuoted(string text, int parentIndent, char quote)
        {
            var builder = new StringBuilder(text);
            while (ClosingQuote(builder.ToString(), quote) < 0)
            {
                if (index >= lines.Count)
                    throw Error("unterminated quoted scalar.");
                builder.Append('\n').Append(lines[index].Trim());
                index++;
            }
            var closing = ClosingQuote(builder.ToString(), quote);
            var after = StripComment(builder.ToString()[(closing + 1)..]).Trim();
            if (after.Length > 0)
                throw Error($"unexpected text after a quoted scalar: '{after}'.");
            return builder.ToString()[..(closing + 1)];
        }

        internal static int ClosingQuote(string text, char quote)
        {
            for (var i = 1; i < text.Length; i++)
            {
                if (quote == '"' && text[i] == '\\')
                {
                    i++;
                    continue;
                }
                if (text[i] == quote)
                {
                    if (quote == '\'' && i + 1 < text.Length && text[i + 1] == '\'')
                    {
                        i++;
                        continue;
                    }
                    return i;
                }
            }
            return -1;
        }

        // Folds the line breaks of a multi-line quoted scalar: one break is a space, n+1 breaks are n newlines.
        private static string FoldQuoted(string text)
        {
            if (!text.Contains('\n'))
                return text;
            var parts = text.Split('\n');
            var builder = new StringBuilder(parts[0].TrimEnd());
            var pendingBreaks = 0;
            for (var i = 1; i < parts.Length; i++)
            {
                var part = parts[i].Trim();
                if (part.Length == 0)
                {
                    pendingBreaks++;
                    continue;
                }
                builder.Append(pendingBreaks > 0 ? new string('\n', pendingBreaks) : " ").Append(part);
                pendingBreaks = 0;
            }
            return builder.ToString();
        }

        internal static string DoubleQuoted(string token)
        {
            var text = FoldQuoted(token[1..^1]);
            var builder = new StringBuilder(text.Length);
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c != '\\' || i + 1 == text.Length)
                {
                    builder.Append(c);
                    continue;
                }
                var escape = text[++i];
                switch (escape)
                {
                    case '0': builder.Append('\0'); break;
                    case 'a': builder.Append('\a'); break;
                    case 'b': builder.Append('\b'); break;
                    case 't' or '\t': builder.Append('\t'); break;
                    case 'n': builder.Append('\n'); break;
                    case 'v': builder.Append('\v'); break;
                    case 'f': builder.Append('\f'); break;
                    case 'r': builder.Append('\r'); break;
                    case 'e': builder.Append('\u001b'); break;
                    case ' ': builder.Append(' '); break;
                    case '"': builder.Append('"'); break;
                    case '/': builder.Append('/'); break;
                    case '\\': builder.Append('\\'); break;
                    case 'N': builder.Append('\u0085'); break;
                    case '_': builder.Append(' '); break;
                    case 'L': builder.Append((char)0x2028); break;
                    case 'P': builder.Append((char)0x2029); break;
                    case 'x': builder.Append(Hex(text, ref i, 2)); break;
                    case 'u': builder.Append(Hex(text, ref i, 4)); break;
                    case 'U': builder.Append(Hex(text, ref i, 8)); break;
                    default: throw new SerializationException("", $"invalid escape '\\{escape}' in a double-quoted scalar.");
                }
            }
            return builder.ToString();
        }

        private static string Hex(string text, ref int i, int digits)
        {
            // The digits follow the escape letter at i.
            if (i + digits >= text.Length)
                throw new SerializationException("", "truncated escape in a double-quoted scalar.");
            if (!int.TryParse(text.AsSpan(i + 1, digits), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                throw new SerializationException("", "invalid escape in a double-quoted scalar.");
            i += digits;
            return char.ConvertFromUtf32(code);
        }

        internal static string SingleQuoted(string token) => FoldQuoted(token[1..^1]).Replace("''", "'");

        private string Key(string text)
        {
            text = text.Trim();
            if (text.StartsWith('"'))
                return DoubleQuoted(text);
            if (text.StartsWith('\''))
                return SingleQuoted(text);
            if (text.StartsWith('?'))
                throw Error("complex keys ('?') are not supported.");
            return text;
        }

        // ------------------------------------------------------------------ scanning

        /// <summary>Resolves a plain scalar with the YAML 1.2 core schema.</summary>
        internal static SerializationValue Resolve(string text)
        {
            switch (text)
            {
                case "" or "~" or "null" or "Null" or "NULL": return SerializationValue.Null;
                case "true" or "True" or "TRUE": return SerializationValue.True;
                case "false" or "False" or "FALSE": return SerializationValue.False;
                case ".inf" or ".Inf" or ".INF" or "+.inf" or "+.Inf" or "+.INF": return SerializationValue.From(double.PositiveInfinity);
                case "-.inf" or "-.Inf" or "-.INF": return SerializationValue.From(double.NegativeInfinity);
                case ".nan" or ".NaN" or ".NAN": return SerializationValue.From(double.NaN);
            }

            if (IntegerPattern().IsMatch(text))
            {
                if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer))
                    return SerializationValue.From(integer);
                if (decimal.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var large))
                    return SerializationValue.From(large);
                return SerializationValue.From(double.Parse(text, CultureInfo.InvariantCulture));
            }
            if (text.StartsWith("0x", StringComparison.Ordinal) && long.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var hex))
                return SerializationValue.From(hex);
            if (text.StartsWith("0o", StringComparison.Ordinal) && text.Length > 2 && text[2..].All(c => c is >= '0' and <= '7'))
                return SerializationValue.From(Convert.ToInt64(text[2..], 8));
            if (FloatPattern().IsMatch(text))
            {
                if (!text.Contains('e') && !text.Contains('E') && decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
                    return SerializationValue.From(amount);
                return SerializationValue.From(double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture));
            }
            return SerializationValue.From(text);
        }

        [GeneratedRegex(@"^[-+]?[0-9]+$")]
        private static partial Regex IntegerPattern();

        [GeneratedRegex(@"^[-+]?(\.[0-9]+|[0-9]+(\.[0-9]*)?)([eE][-+]?[0-9]+)?$")]
        private static partial Regex FloatPattern();

        private static bool IsSequenceEntry(string content) => content == "-" || content.StartsWith("- ", StringComparison.Ordinal);

        private static bool StartsInline(string content) => content.Length > 0 && content[0] is '[' or '{' or '"' or '\'' or '|' or '>' or '*';

        // The position of the ':' that separates a mapping key from its value, or -1.
        internal static int MappingColon(string content)
        {
            if (content.Length == 0 || content[0] is '[' or '{' or '|' or '>' or '*' or '#')
                return -1;
            var start = 0;
            if (content[0] is '"' or '\'')
            {
                var closing = ClosingQuote(content, content[0]);
                if (closing < 0)
                    return -1;
                start = closing + 1;
                var after = content[start..].TrimStart();
                return after.StartsWith(':') && (after.Length == 1 || after[1] == ' ') ? content.IndexOf(':', start) : -1;
            }
            for (var i = start; i < content.Length; i++)
            {
                if (content[i] == ':' && (i + 1 == content.Length || content[i + 1] == ' '))
                    return i;
            }
            return -1;
        }

        // Removes a comment: a '#' at the start or after white space, outside quotes.
        internal static string StripComment(string content)
        {
            char? quote = null;
            for (var i = 0; i < content.Length; i++)
            {
                var c = content[i];
                if (quote is not null)
                {
                    if (c == '\\' && quote == '"')
                        i++;
                    else if (c == '\'' && quote == '\'' && i + 1 < content.Length && content[i + 1] == '\'')
                        i++;   // '' is an escaped quote inside a single-quoted scalar
                    else if (c == quote)
                        quote = null;
                    continue;
                }
                if (c is '"' or '\'' && (i == 0 || content[i - 1] is ' ' or ':' or '[' or '{' or ',' or '-'))
                    quote = c;
                else if (c == '#' && (i == 0 || content[i - 1] is ' ' or '\t'))
                    return content[..i].TrimEnd();
            }
            return content.TrimEnd();
        }

        private static bool Balanced(string text)
        {
            var depth = 0;
            char? quote = null;
            foreach (var c in text)
            {
                if (quote is not null)
                {
                    if (c == quote)
                        quote = null;
                    continue;
                }
                if (c is '"' or '\'')
                    quote = c;
                else if (c is '[' or '{')
                    depth++;
                else if (c is ']' or '}')
                    depth--;
            }
            return depth <= 0;
        }

        private bool SkipToContent()
        {
            while (index < lines.Count && (IsBlank(lines[index]) || StripComment(lines[index].Trim()).Length == 0))
                index++;
            if (index < lines.Count && lines[index].TrimEnd() == "...")
                return false;
            return index < lines.Count;
        }

        private int Indent(string line)
        {
            var count = 0;
            while (count < line.Length && line[count] == ' ')
                count++;
            if (count < line.Length && line[count] == '\t')
                throw Error("tabs cannot be used for indentation.");
            return count;
        }

        private static bool IsBlank(string line) => string.IsNullOrWhiteSpace(line);

        internal SerializationException Error(string problem) => new("", $"line {Math.Min(index + 1, lines.Count)}: {problem}");

        // ------------------------------------------------------------------ flow collections

        private sealed class FlowParser(string text, YamlReader owner, int depth)
        {
            private int position;

            public SerializationValue Parse()
            {
                var value = Value(depth);
                SkipSpaces();
                if (position < text.Length)
                    throw owner.Error($"unexpected '{text[position]}' after a flow collection.");
                return value;
            }

            private SerializationValue Value(int level)
            {
                if (level > owner.maxDepth)
                    throw owner.Error($"nested deeper than {owner.maxDepth} levels.");
                SkipSpaces();
                if (position >= text.Length)
                    return SerializationValue.Null;

                switch (text[position])
                {
                    case '[':
                        position++;
                        var items = new List<SerializationValue>();
                        while (true)
                        {
                            SkipSpaces();
                            if (Peek(']'))
                                break;
                            items.Add(Value(level + 1));
                            SkipSpaces();
                            if (Peek(','))
                                continue;
                            if (Peek(']'))
                                break;
                            throw owner.Error("expected ',' or ']' in a flow sequence.");
                        }
                        return SerializationValue.Array(items);
                    case '{':
                        position++;
                        var members = new List<KeyValuePair<string, SerializationValue>>();
                        while (true)
                        {
                            SkipSpaces();
                            if (Peek('}'))
                                break;
                            var key = Scalar(inMap: true);
                            SkipSpaces();
                            var value = Peek(':') ? Value(level + 1) : SerializationValue.Null;
                            members.Add(new(key.Kind == ValueKind.String ? key.AsString() : key.ToString(), value));
                            SkipSpaces();
                            if (Peek(','))
                                continue;
                            if (Peek('}'))
                                break;
                            throw owner.Error("expected ',' or '}' in a flow mapping.");
                        }
                        return SerializationValue.Object(members);
                    default:
                        return Scalar(inMap: false);
                }
            }

            private SerializationValue Scalar(bool inMap)
            {
                SkipSpaces();
                if (position < text.Length && text[position] is '"' or '\'')
                {
                    var quote = text[position];
                    var closing = ClosingQuote(text[position..], quote);
                    if (closing < 0)
                        throw owner.Error("unterminated quoted scalar.");
                    var token = text.Substring(position, closing + 1);
                    position += closing + 1;
                    return SerializationValue.From(quote == '"' ? DoubleQuoted(token) : SingleQuoted(token));
                }

                var start = position;
                while (position < text.Length)
                {
                    var c = text[position];
                    if (c is ',' or ']' or '}' || (c == ':' && (position + 1 == text.Length || text[position + 1] is ' ' or ',' or ']' or '}')))
                        break;
                    position++;
                }
                var plain = text[start..position].Trim();
                return inMap ? SerializationValue.From(plain) : Resolve(plain);
            }

            private bool Peek(char c)
            {
                if (position < text.Length && text[position] == c)
                {
                    position++;
                    return true;
                }
                return false;
            }

            private void SkipSpaces()
            {
                while (position < text.Length && text[position] is ' ' or '\t')
                    position++;
            }
        }
    }
}

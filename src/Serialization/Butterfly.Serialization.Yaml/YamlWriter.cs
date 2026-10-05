using System.Globalization;
using System.Text;

namespace Butterfly.Serialization.Yaml
{
    internal static class YamlWriter
    {
        private const string Indicators = "-?:,[]{}#&*!|>'\"%@`";

        public static string Write(SerializationValue value)
        {
            var builder = new StringBuilder();
            if (IsBlock(value))
                WriteBlock(builder, value, 0, firstLineIndented: true);
            else
                builder.Append(Inline(value)).Append('\n');
            return builder.ToString();
        }

        // Non-empty collections are written as blocks; everything else fits on the line of its key or dash.
        private static bool IsBlock(SerializationValue value) =>
            (value.Kind == ValueKind.Object && value.Members.Count > 0) || (value.Kind == ValueKind.Array && value.Items.Count > 0);

        private static void WriteBlock(StringBuilder builder, SerializationValue value, int indent, bool firstLineIndented)
        {
            var padding = new string(' ', indent);
            var first = true;

            if (value.Kind == ValueKind.Object)
            {
                foreach (var (name, member) in value.Members)
                {
                    if (!first || firstLineIndented)
                        builder.Append(padding);
                    first = false;
                    builder.Append(Key(name)).Append(':');
                    if (member.Kind == ValueKind.Array && IsBlock(member))
                    {
                        // Sequences under a key are indented one level, the most common style.
                        builder.Append('\n');
                        WriteBlock(builder, member, indent + 2, firstLineIndented: true);
                    }
                    else if (IsBlock(member))
                    {
                        builder.Append('\n');
                        WriteBlock(builder, member, indent + 2, firstLineIndented: true);
                    }
                    else
                    {
                        builder.Append(' ').Append(Inline(member)).Append('\n');
                    }
                }
                return;
            }

            foreach (var item in value.Items)
            {
                if (!first || firstLineIndented)
                    builder.Append(padding);
                first = false;
                builder.Append("- ");
                if (IsBlock(item))
                    WriteBlock(builder, item, indent + 2, firstLineIndented: false);
                else
                    builder.Append(Inline(item)).Append('\n');
            }
        }

        private static string Inline(SerializationValue value) => value.Kind switch
        {
            ValueKind.Null => "null",
            ValueKind.Boolean => value.AsBoolean() ? "true" : "false",
            ValueKind.Integer => value.AsInt64().ToString(CultureInfo.InvariantCulture),
            ValueKind.Number => Number(value.AsDouble()),
            ValueKind.Decimal => value.AsDecimal().ToString(CultureInfo.InvariantCulture),
            ValueKind.String => Scalar(value.AsString()),
            ValueKind.Bytes => "!!binary " + Quote(Convert.ToBase64String(value.AsBytes())),
            ValueKind.Timestamp => Quote(ScalarText.FormatTimestamp(value.AsTimestamp())),
            ValueKind.Array => "[]",
            _ => "{}"
        };

        private static string Number(double number) => double.IsNaN(number) ? ".nan"
            : double.IsPositiveInfinity(number) ? ".inf"
            : double.IsNegativeInfinity(number) ? "-.inf"
            : number.ToString("R", CultureInfo.InvariantCulture);

        private static string Key(string name) => Scalar(name);

        // Plain when it is unambiguous; double-quoted otherwise.
        private static string Scalar(string text) => IsPlainSafe(text) ? text : Quote(text);

        internal static bool IsPlainSafe(string text)
        {
            if (text.Length == 0 || text != text.Trim() || Indicators.Contains(text[0]))
                return false;
            // Text that would read back as null, a boolean or a number.
            if (YamlReader.Resolve(text).Kind != ValueKind.String)
                return false;
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c < 0x20 || c is '\u007f' or '\u0085' or (char)0x2028 or (char)0x2029 or '"' or '\\' or ',' or '[' or ']' or '{' or '}')
                    return false;
                if (c == ':' && (i + 1 == text.Length || text[i + 1] == ' '))
                    return false;
                if (c == '#' && text[i - 1] == ' ')
                    return false;
            }
            return true;
        }

        internal static string Quote(string text)
        {
            var builder = new StringBuilder(text.Length + 2).Append('"');
            foreach (var c in text)
            {
                switch (c)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    case '\0': builder.Append("\\0"); break;
                    default:
                        if (c < 0x20 || c is '\u007f' or '\u0085' or (char)0x2028 or (char)0x2029 or (char)0xFEFF)
                            builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            builder.Append(c);
                        break;
                }
            }
            return builder.Append('"').ToString();
        }
    }
}

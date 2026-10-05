using System.Buffers;
using System.Globalization;
using System.Text;

namespace Butterfly.Serialization.Csv
{
    /// <summary>
    /// CSV (RFC 4180): a header row with the member names, then one row per object of a list (or a single row for an
    /// object). Nested objects become "parent.child" columns; lists inside rows cannot be represented and are rejected.
    /// Empty cells are missing values. Names are as declared by default.
    /// </summary>
    public sealed class CsvFormat : SerializationFormat
    {
        public CsvFormat(char separator = ',')
        {
            if (separator is '"' or '\r' or '\n' or '.')
                throw new ArgumentException("The separator cannot be a quote, a line break or a dot.", nameof(separator));
            Separator = separator;
        }

        public static CsvFormat Instance { get; } = new();
        /// <summary>Semicolon-separated, as spreadsheets expect where the comma is the decimal separator.</summary>
        public static CsvFormat Semicolon { get; } = new(';');

        public char Separator { get; }

        public override string Name => "csv";
        public override string MediaType => "text/csv";
        public override bool IsText => true;
        public ValueConventions Conventions { get; } = new(NamingPolicy.AsDeclared, EnumsAsNames: true, ScalarsFromText: true);

        public override void Write(IBufferWriter<byte> output, SerializableType type, object? value, SerializationProfile profile)
        {
            var tree = ValueConverter.ToValue(type, value, Conventions, profile);
            var rows = tree.Kind switch
            {
                ValueKind.Array => tree.Items,
                ValueKind.Object => [tree],
                ValueKind.Null => [],
                _ => throw new SerializationException("", "CSV holds objects or lists of objects.")
            };

            var columns = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var flattened = new List<Dictionary<string, string>>(rows.Count);
            for (var i = 0; i < rows.Count; i++)
            {
                if (rows[i].Kind != ValueKind.Object)
                    throw new SerializationException(SerializationException.Item("", i), "CSV rows must be objects.");
                var cells = new Dictionary<string, string>(StringComparer.Ordinal);
                Flatten(rows[i], "", cells, SerializationException.Item("", i));
                foreach (var column in cells.Keys)
                {
                    if (seen.Add(column))
                        columns.Add(column);
                }
                flattened.Add(cells);
            }

            var builder = new StringBuilder();
            AppendRow(builder, columns);
            foreach (var cells in flattened)
                AppendRow(builder, columns.Select(column => cells.GetValueOrDefault(column, "")));
            output.Write(Encoding.UTF8.GetBytes(builder.ToString()));
        }

        public override object? Read(ReadOnlyMemory<byte> input, SerializableType type, SerializationProfile profile)
        {
            string text;
            try
            {
                text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(input.Span).TrimStart('﻿');
            }
            catch (DecoderFallbackException)
            {
                throw new SerializationException("", "the CSV text is not valid UTF-8.");
            }

            var records = Parse(text);
            if (records.Count == 0)
                return type is ListType ? type.CreateDefault() : null;

            var header = records[0];
            var rows = new List<SerializationValue>(records.Count - 1);
            for (var r = 1; r < records.Count; r++)
            {
                var record = records[r];
                if (record.Count != header.Count)
                    throw new SerializationException(SerializationException.Item("", r - 1), $"the row has {record.Count} cells; the header has {header.Count}.");
                rows.Add(Unflatten(header, record));
            }

            if (type is ListType)
                return ValueConverter.FromValue(type, SerializationValue.Array(rows), Conventions, profile);
            if (rows.Count > 1)
                throw new SerializationException("", $"expected a single row for {type}, found {rows.Count}.");
            return rows.Count == 0 ? null : ValueConverter.FromValue(type, rows[0], Conventions, profile);
        }

        private static void Flatten(SerializationValue value, string prefix, Dictionary<string, string> cells, string path)
        {
            foreach (var (name, member) in value.Members)
            {
                var column = prefix.Length == 0 ? name : prefix + "." + name;
                switch (member.Kind)
                {
                    case ValueKind.Object:
                        Flatten(member, column, cells, SerializationException.Child(path, name));
                        break;
                    case ValueKind.Array:
                        throw new SerializationException(SerializationException.Child(path, name), "lists cannot be written to CSV.");
                    case ValueKind.Null:
                        cells[column] = "";
                        break;
                    default:
                        cells[column] = Cell(member);
                        break;
                }
            }
        }

        private static string Cell(SerializationValue value) => value.Kind switch
        {
            ValueKind.Boolean => value.AsBoolean() ? "true" : "false",
            ValueKind.Integer => value.AsInt64().ToString(CultureInfo.InvariantCulture),
            ValueKind.Number => value.AsDouble().ToString("R", CultureInfo.InvariantCulture),
            ValueKind.Decimal => value.AsDecimal().ToString(CultureInfo.InvariantCulture),
            ValueKind.Bytes => Convert.ToBase64String(value.AsBytes()),
            ValueKind.Timestamp => ScalarText.FormatTimestamp(value.AsTimestamp()),
            _ => value.AsString()
        };

        // "a.b" columns become nested objects again; empty cells are left out so defaults apply.
        private static SerializationValue Unflatten(List<string> header, List<string> record)
        {
            var root = new Node();
            for (var i = 0; i < header.Count; i++)
            {
                if (record[i].Length == 0)
                    continue;
                var parts = header[i].Split('.');
                var node = root;
                foreach (var part in parts[..^1])
                    node = node.Children.TryGetValue(part, out var child) ? child : node.Add(part);
                node.Values[parts[^1]] = record[i];
                node.Order.Add(parts[^1]);
            }
            return root.ToValue();
        }

        private sealed class Node
        {
            public Dictionary<string, Node> Children { get; } = new(StringComparer.Ordinal);
            public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);
            public List<string> Order { get; } = [];

            public Node Add(string name)
            {
                var child = new Node();
                Children[name] = child;
                Order.Add(name);
                return child;
            }

            public SerializationValue ToValue() => SerializationValue.Object(Order.Distinct().Select(name =>
                new KeyValuePair<string, SerializationValue>(name, Children.TryGetValue(name, out var child) ? child.ToValue() : SerializationValue.From(Values[name]))));
        }

        private void AppendRow(StringBuilder builder, IEnumerable<string> cells)
        {
            var first = true;
            foreach (var cell in cells)
            {
                if (!first)
                    builder.Append(Separator);
                first = false;
                // Quoted when it would otherwise be misread (RFC 4180); leading spaces are kept that way too.
                if (cell.IndexOfAny([Separator, '"', '\r', '\n']) >= 0 || (cell.Length > 0 && (cell[0] == ' ' || cell[^1] == ' ')))
                    builder.Append('"').Append(cell.Replace("\"", "\"\"")).Append('"');
                else
                    builder.Append(cell);
            }
            builder.Append("\r\n");
        }

        private List<List<string>> Parse(string text)
        {
            var records = new List<List<string>>();
            var record = new List<string>();
            var cell = new StringBuilder();
            var quoted = false;
            var cellStarted = false;

            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (quoted)
                {
                    if (c == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"')
                        {
                            cell.Append('"');
                            i++;
                        }
                        else
                        {
                            quoted = false;
                        }
                    }
                    else
                    {
                        cell.Append(c);
                    }
                    continue;
                }

                if (c == '"' && cell.Length == 0)
                {
                    quoted = true;
                    cellStarted = true;
                }
                else if (c == Separator)
                {
                    record.Add(cell.ToString());
                    cell.Clear();
                    cellStarted = true;
                }
                else if (c is '\r' or '\n')
                {
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                        i++;
                    if (cellStarted || cell.Length > 0 || record.Count > 0)
                    {
                        record.Add(cell.ToString());
                        records.Add(record);
                    }
                    record = [];
                    cell.Clear();
                    cellStarted = false;
                }
                else
                {
                    cell.Append(c);
                    cellStarted = true;
                }
            }

            if (quoted)
                throw new SerializationException("", "unterminated quoted cell.");
            if (cellStarted || cell.Length > 0 || record.Count > 0)
            {
                record.Add(cell.ToString());
                records.Add(record);
            }
            return records;
        }
    }
}

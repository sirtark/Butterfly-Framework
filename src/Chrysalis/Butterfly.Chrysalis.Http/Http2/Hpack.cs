using System.Text;

namespace Butterfly.Chrysalis.Http.Http2
{
    internal sealed class HpackException(string message) : Exception(message);

    /// <summary>HPACK header compression (RFC 7541).</summary>
    internal static class HpackTable
    {
        public static readonly (string Name, string Value)[] Static =
        [
            (":authority", ""), (":method", "GET"), (":method", "POST"), (":path", "/"), (":path", "/index.html"),
            (":scheme", "http"), (":scheme", "https"), (":status", "200"), (":status", "204"), (":status", "206"),
            (":status", "304"), (":status", "400"), (":status", "404"), (":status", "500"), ("accept-charset", ""),
            ("accept-encoding", "gzip, deflate"), ("accept-language", ""), ("accept-ranges", ""), ("accept", ""), ("access-control-allow-origin", ""),
            ("age", ""), ("allow", ""), ("authorization", ""), ("cache-control", ""), ("content-disposition", ""),
            ("content-encoding", ""), ("content-language", ""), ("content-length", ""), ("content-location", ""), ("content-range", ""),
            ("content-type", ""), ("cookie", ""), ("date", ""), ("etag", ""), ("expect", ""),
            ("expires", ""), ("from", ""), ("host", ""), ("if-match", ""), ("if-modified-since", ""),
            ("if-none-match", ""), ("if-range", ""), ("if-unmodified-since", ""), ("last-modified", ""), ("link", ""),
            ("location", ""), ("max-forwards", ""), ("proxy-authenticate", ""), ("proxy-authorization", ""), ("range", ""),
            ("referer", ""), ("refresh", ""), ("retry-after", ""), ("server", ""), ("set-cookie", ""),
            ("strict-transport-security", ""), ("transfer-encoding", ""), ("user-agent", ""), ("vary", ""), ("via", ""),
            ("www-authenticate", "")
        ];

        // 1-based indexes, as HPACK numbers them.
        public static readonly Dictionary<(string, string), int> ByNameAndValue = Static
            .Select((entry, index) => (entry, index: index + 1))
            .GroupBy(item => item.entry)
            .ToDictionary(group => group.Key, group => group.First().index);

        public static readonly Dictionary<string, int> ByName = Static
            .Select((entry, index) => (entry.Name, index: index + 1))
            .GroupBy(item => item.Name)
            .ToDictionary(group => group.Key, group => group.First().index);
    }

    /// <summary>Decodes header blocks of one connection; its dynamic table lives as long as the connection.</summary>
    internal sealed class HpackDecoder(int maxTableSize)
    {
        private readonly LinkedList<(string Name, string Value)> dynamicTable = new();   // newest first
        private readonly int maxTableSize = maxTableSize;
        private int tableSize;
        private int tableCapacity = maxTableSize;

        /// <exception cref="HpackException">The block is malformed: a COMPRESSION_ERROR for the whole connection.</exception>
        public List<(string Name, string Value)> Decode(ReadOnlySpan<byte> block)
        {
            var headers = new List<(string, string)>();
            var position = 0;
            var headerSeen = false;

            while (position < block.Length)
            {
                var first = block[position];
                if ((first & 0x80) != 0)
                {
                    headers.Add(Lookup(ReadInteger(block, ref position, 7)));
                    headerSeen = true;
                }
                else if ((first & 0x40) != 0)
                {
                    var header = ReadLiteral(block, ref position, 6);
                    Insert(header);
                    headers.Add(header);
                    headerSeen = true;
                }
                else if ((first & 0x20) != 0)
                {
                    // Dynamic table size update: only at the start of a block, and within our advertised limit.
                    if (headerSeen)
                        throw new HpackException("A table size update must come first in a header block.");
                    var size = ReadInteger(block, ref position, 5);
                    if (size > maxTableSize)
                        throw new HpackException("The table size update exceeds SETTINGS_HEADER_TABLE_SIZE.");
                    tableCapacity = size;
                    Evict(0);
                }
                else
                {
                    // Literal without indexing (0000) or never indexed (0001).
                    headers.Add(ReadLiteral(block, ref position, 4));
                    headerSeen = true;
                }
            }
            return headers;
        }

        private (string Name, string Value) ReadLiteral(ReadOnlySpan<byte> block, ref int position, int prefix)
        {
            var index = ReadInteger(block, ref position, prefix);
            var name = index == 0 ? ReadString(block, ref position) : Lookup(index).Name;
            return (name, ReadString(block, ref position));
        }

        private (string Name, string Value) Lookup(int index)
        {
            if (index <= 0)
                throw new HpackException("Header index 0 is invalid.");
            if (index <= HpackTable.Static.Length)
                return HpackTable.Static[index - 1];

            var dynamicIndex = index - HpackTable.Static.Length - 1;
            if (dynamicIndex >= dynamicTable.Count)
                throw new HpackException($"Header index {index} is outside the table.");
            return dynamicTable.ElementAt(dynamicIndex);
        }

        private void Insert((string Name, string Value) header)
        {
            var size = EntrySize(header);
            // An entry bigger than the table empties it and is not stored (RFC 7541 4.4).
            Evict(size);
            if (size <= tableCapacity)
            {
                dynamicTable.AddFirst(header);
                tableSize += size;
            }
        }

        private void Evict(int incoming)
        {
            while (dynamicTable.Count > 0 && tableSize + incoming > tableCapacity)
            {
                tableSize -= EntrySize(dynamicTable.Last!.Value);
                dynamicTable.RemoveLast();
            }
        }

        private static int EntrySize((string Name, string Value) header) => header.Name.Length + header.Value.Length + 32;

        internal static int ReadInteger(ReadOnlySpan<byte> block, ref int position, int prefix)
        {
            var mask = (1 << prefix) - 1;
            var value = block[position++] & mask;
            if (value < mask)
                return value;

            for (var shift = 0; ; shift += 7)
            {
                if (position >= block.Length)
                    throw new HpackException("Truncated integer.");
                if (shift > 21)
                    throw new HpackException("Integer too large.");
                var current = block[position++];
                value += (current & 0x7F) << shift;
                if ((current & 0x80) == 0)
                    return value;
            }
        }

        private static string ReadString(ReadOnlySpan<byte> block, ref int position)
        {
            if (position >= block.Length)
                throw new HpackException("Truncated string.");
            var huffman = (block[position] & 0x80) != 0;
            var length = ReadInteger(block, ref position, 7);
            if (length > block.Length - position)
                throw new HpackException("Truncated string.");

            var bytes = block.Slice(position, length);
            position += length;
            // Latin-1 keeps every byte (obs-text) as one char.
            return Encoding.Latin1.GetString(huffman ? Huffman.Decode(bytes) : bytes);
        }
    }

    /// <summary>
    /// Encodes response headers with the static table and plain literals, never indexing: the peer needs no dynamic table
    /// state from us, and nothing (cookies, tokens) is kept in its compression context.
    /// </summary>
    internal static class HpackEncoder
    {
        public static void Encode(List<byte> output, string name, string value)
        {
            if (HpackTable.ByNameAndValue.TryGetValue((name, value), out var index))
            {
                WriteInteger(output, 0x80, 7, index);
                return;
            }

            if (HpackTable.ByName.TryGetValue(name, out index))
            {
                WriteInteger(output, 0x00, 4, index);
            }
            else
            {
                output.Add(0x00);
                WriteString(output, name);
            }
            WriteString(output, value);
        }

        private static void WriteString(List<byte> output, string text)
        {
            var bytes = Encoding.Latin1.GetBytes(text);
            WriteInteger(output, 0x00, 7, bytes.Length);
            output.AddRange(bytes);
        }

        internal static void WriteInteger(List<byte> output, byte pattern, int prefix, int value)
        {
            var mask = (1 << prefix) - 1;
            if (value < mask)
            {
                output.Add((byte)(pattern | value));
                return;
            }

            output.Add((byte)(pattern | mask));
            value -= mask;
            while (value >= 0x80)
            {
                output.Add((byte)(value % 0x80 + 0x80));
                value /= 0x80;
            }
            output.Add((byte)value);
        }
    }
}

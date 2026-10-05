using System.Buffers.Binary;
using System.Globalization;
using System.Text;

using Butterfly.Networking.Sockets;

namespace Butterfly.Communication.Dns
{
    /// <summary>Encodes queries and decodes responses in the RFC 1035 wire format.</summary>
    internal static class DnsMessage
    {
        public const int HeaderLength = 12;
        private const int MaxNameLength = 255;
        private const int MaxLabelLength = 63;
        private const int MaxPointerJumps = 64;
        private static readonly IdnMapping s_idn = new();

        public static byte[] EncodeQuery(ushort id, DnsQuestion question, bool recursionDesired, ushort ednsPayloadSize)
        {
            var buffer = new List<byte>(HeaderLength + question.Name.Length + 32);

            WriteUInt16(buffer, id);
            WriteUInt16(buffer, (ushort)(recursionDesired ? 0x0100 : 0)); // QR=0, OPCODE=QUERY, RD
            WriteUInt16(buffer, 1);                                       // QDCOUNT
            WriteUInt16(buffer, 0);                                       // ANCOUNT
            WriteUInt16(buffer, 0);                                       // NSCOUNT
            WriteUInt16(buffer, (ushort)(ednsPayloadSize > 0 ? 1 : 0));   // ARCOUNT

            WriteName(buffer, question.Name);
            WriteUInt16(buffer, (ushort)question.Type);
            WriteUInt16(buffer, (ushort)question.Class);

            // EDNS(0) OPT pseudo-record (RFC 6891): root name, type OPT, class = UDP payload size, no options.
            if (ednsPayloadSize > 0)
            {
                buffer.Add(0);
                WriteUInt16(buffer, (ushort)DnsRecordType.OPT);
                WriteUInt16(buffer, ednsPayloadSize);
                WriteUInt32(buffer, 0);
                WriteUInt16(buffer, 0);
            }

            return [.. buffer];
        }

        /// <summary>Converts a host name (Unicode names become punycode) to the wire format.</summary>
        public static void WriteName(List<byte> buffer, string name)
        {
            string ascii = ToAscii(name.TrimEnd('.'));
            if (ascii.Length == 0)
            {
                buffer.Add(0);
                return;
            }

            if (ascii.Length > MaxNameLength - 2)
                throw new ArgumentException($"'{name}' is longer than {MaxNameLength} bytes.", nameof(name));

            foreach (string label in ascii.Split('.'))
            {
                if (label.Length is 0 or > MaxLabelLength)
                    throw new ArgumentException($"'{name}' has an empty label or one longer than {MaxLabelLength} characters.", nameof(name));

                buffer.Add((byte)label.Length);
                foreach (char c in label)
                    buffer.Add((byte)c);
            }

            buffer.Add(0);
        }

        /// <summary>The ASCII (punycode) form of a host name, which is what servers echo back.</summary>
        public static string ToAscii(string name)
        {
            foreach (char c in name)
            {
                if (c > 0x7F)
                    return s_idn.GetAscii(name);
            }

            return name;
        }

        public static DnsResponse DecodeResponse(ReadOnlySpan<byte> message)
        {
            if (message.Length < HeaderLength)
                throw new DnsException("The DNS response is shorter than its header.");

            ushort id = BinaryPrimitives.ReadUInt16BigEndian(message);
            ushort flags = BinaryPrimitives.ReadUInt16BigEndian(message[2..]);
            int questionCount = BinaryPrimitives.ReadUInt16BigEndian(message[4..]);
            int answerCount = BinaryPrimitives.ReadUInt16BigEndian(message[6..]);
            int authorityCount = BinaryPrimitives.ReadUInt16BigEndian(message[8..]);
            int additionalCount = BinaryPrimitives.ReadUInt16BigEndian(message[10..]);

            if ((flags & 0x8000) == 0)
                throw new DnsException("The DNS message is a query, not a response.");

            int offset = HeaderLength;
            var questions = new List<DnsQuestion>(questionCount);
            for (int i = 0; i < questionCount; i++)
            {
                string name = ReadName(message, ref offset);
                Require(message, offset, 4);
                questions.Add(new DnsQuestion(name,
                    (DnsRecordType)BinaryPrimitives.ReadUInt16BigEndian(message[offset..]),
                    (DnsClass)BinaryPrimitives.ReadUInt16BigEndian(message[(offset + 2)..])));
                offset += 4;
            }

            var answers = ReadRecords(message, ref offset, answerCount);
            var authorities = ReadRecords(message, ref offset, authorityCount);
            var additionals = ReadRecords(message, ref offset, additionalCount);

            return new DnsResponse(id, (DnsResponseCode)(flags & 0x000F),
                authoritative: (flags & 0x0400) != 0, truncated: (flags & 0x0200) != 0, recursionAvailable: (flags & 0x0080) != 0,
                questions, answers, authorities, additionals);
        }

        private static List<DnsRecord> ReadRecords(ReadOnlySpan<byte> message, ref int offset, int count)
        {
            var records = new List<DnsRecord>(count);
            for (int i = 0; i < count; i++)
            {
                string name = ReadName(message, ref offset);
                Require(message, offset, 10);

                var type = (DnsRecordType)BinaryPrimitives.ReadUInt16BigEndian(message[offset..]);
                ushort @class = BinaryPrimitives.ReadUInt16BigEndian(message[(offset + 2)..]);
                uint ttl = BinaryPrimitives.ReadUInt32BigEndian(message[(offset + 4)..]);
                int length = BinaryPrimitives.ReadUInt16BigEndian(message[(offset + 8)..]);
                offset += 10;

                Require(message, offset, length);
                int dataOffset = offset;
                offset += length;

                // OPT is a pseudo-record describing the transport, not data.
                if (type == DnsRecordType.OPT)
                    continue;

                records.Add(DecodeRecord(message, name, type, (DnsClass)@class, ttl, dataOffset, length));
            }

            return records;
        }

        private static DnsRecord DecodeRecord(ReadOnlySpan<byte> message, string name, DnsRecordType type, DnsClass @class, uint ttl, int offset, int length)
        {
            ReadOnlySpan<byte> data = message.Slice(offset, length);
            byte[] raw = data.ToArray();

            switch (type)
            {
                case DnsRecordType.A when length == SocketAddress.IPv4AddressLength:
                case DnsRecordType.AAAA when length == SocketAddress.IPv6AddressLength:
                    return new AddressRecord(name, type, @class, ttl, raw, new SocketAddress(data, 0));

                case DnsRecordType.CNAME or DnsRecordType.NS or DnsRecordType.PTR:
                    return new NameRecord(name, type, @class, ttl, raw, ReadName(message, ref offset));

                case DnsRecordType.MX when length >= 3:
                {
                    ushort preference = BinaryPrimitives.ReadUInt16BigEndian(data);
                    offset += 2;
                    return new MxRecord(name, @class, ttl, raw, preference, ReadName(message, ref offset));
                }

                case DnsRecordType.SRV when length >= 7:
                {
                    ushort priority = BinaryPrimitives.ReadUInt16BigEndian(data);
                    ushort weight = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
                    ushort port = BinaryPrimitives.ReadUInt16BigEndian(data[4..]);
                    offset += 6;
                    return new SrvRecord(name, @class, ttl, raw, priority, weight, port, ReadName(message, ref offset));
                }

                case DnsRecordType.SOA:
                {
                    string primary = ReadName(message, ref offset);
                    string mailbox = ReadName(message, ref offset);
                    Require(message, offset, 20);
                    ReadOnlySpan<byte> numbers = message[offset..];
                    return new SoaRecord(name, @class, ttl, raw, primary, mailbox,
                        BinaryPrimitives.ReadUInt32BigEndian(numbers), BinaryPrimitives.ReadUInt32BigEndian(numbers[4..]),
                        BinaryPrimitives.ReadUInt32BigEndian(numbers[8..]), BinaryPrimitives.ReadUInt32BigEndian(numbers[12..]),
                        BinaryPrimitives.ReadUInt32BigEndian(numbers[16..]));
                }

                case DnsRecordType.TXT:
                {
                    var strings = new List<string>();
                    for (int i = 0; i < data.Length;)
                    {
                        int size = data[i++];
                        if (i + size > data.Length)
                            throw new DnsException("A TXT record string runs past the end of its data.");
                        strings.Add(Encoding.UTF8.GetString(data.Slice(i, size)));
                        i += size;
                    }
                    return new TxtRecord(name, @class, ttl, raw, strings);
                }

                case DnsRecordType.CAA when length >= 2:
                {
                    int tagLength = data[1];
                    if (2 + tagLength > data.Length)
                        throw new DnsException("A CAA record tag runs past the end of its data.");
                    return new CaaRecord(name, @class, ttl, raw, data[0],
                        Encoding.ASCII.GetString(data.Slice(2, tagLength)), Encoding.UTF8.GetString(data[(2 + tagLength)..]));
                }

                default:
                    return new DnsRecord(name, type, @class, ttl, raw);
            }
        }

        /// <summary>Reads a possibly compressed name (RFC 1035 4.1.4) and advances <paramref name="offset"/> past it.</summary>
        public static string ReadName(ReadOnlySpan<byte> message, ref int offset)
        {
            var builder = new StringBuilder();
            int position = offset;
            int jumps = 0;
            bool jumped = false;

            while (true)
            {
                Require(message, position, 1);
                byte length = message[position];

                if ((length & 0xC0) == 0xC0)
                {
                    Require(message, position, 2);
                    if (++jumps > MaxPointerJumps)
                        throw new DnsException("The DNS response contains a compression loop.");

                    int pointer = BinaryPrimitives.ReadUInt16BigEndian(message[position..]) & 0x3FFF;
                    if (!jumped)
                        offset = position + 2;

                    jumped = true;
                    position = pointer;
                    continue;
                }

                if ((length & 0xC0) != 0)
                    throw new DnsException("The DNS response uses an unsupported label type.");

                position++;
                if (length == 0)
                    break;

                Require(message, position, length);
                if (builder.Length > 0)
                    builder.Append('.');

                foreach (byte b in message.Slice(position, length))
                {
                    // Printable characters as-is; everything else as \DDD (RFC 4343 presentation format).
                    if (b is > 0x20 and < 0x7F && b != '.' && b != '\\')
                        builder.Append((char)b);
                    else
                        builder.Append('\\').Append(b.ToString("D3", CultureInfo.InvariantCulture));
                }

                if (builder.Length > MaxNameLength * 4)
                    throw new DnsException("A name in the DNS response is too long.");

                position += length;
            }

            if (!jumped)
                offset = position;

            return builder.ToString();
        }

        private static void Require(ReadOnlySpan<byte> message, int offset, int count)
        {
            if (offset < 0 || offset + count > message.Length)
                throw new DnsException("The DNS response is truncated or malformed.");
        }

        private static void WriteUInt16(List<byte> buffer, ushort value)
        {
            buffer.Add((byte)(value >> 8));
            buffer.Add((byte)value);
        }

        private static void WriteUInt32(List<byte> buffer, uint value)
        {
            WriteUInt16(buffer, (ushort)(value >> 16));
            WriteUInt16(buffer, (ushort)value);
        }
    }
}

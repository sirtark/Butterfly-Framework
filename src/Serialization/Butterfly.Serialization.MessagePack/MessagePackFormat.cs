using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Butterfly.Serialization.MessagePack
{
    /// <summary>
    /// MessagePack (https://github.com/msgpack/msgpack/blob/master/spec.md). Objects are maps keyed by member name
    /// (camelCase by default), enums are numbers by default, integers use the shortest encoding, timestamps use the
    /// standard extension (type -1), Guid travels as a string, and decimals are float64 when exact (text otherwise).
    /// </summary>
    public sealed class MessagePackFormat : ValueFormat
    {
        private const sbyte TimestampExtension = -1;

        public static MessagePackFormat Instance { get; } = new();

        public override string Name => "msgpack";
        public override string MediaType => "application/msgpack";
        public override bool IsText => false;
        public override ValueConventions Conventions { get; } = new(NamingPolicy.CamelCase, EnumsAsNames: false, ScalarsFromText: false);

        public override void WriteValue(IBufferWriter<byte> output, SerializationValue value, SerializationProfile profile) => Write(output, value);

        public override SerializationValue ReadValue(ReadOnlyMemory<byte> input, SerializationProfile profile)
        {
            var reader = new Reader(input.Span, profile.MaxDepth);
            var value = reader.Read(0);
            if (!reader.End)
                throw new SerializationException("", "unexpected data after the value.");
            return value;
        }

        private static void Write(IBufferWriter<byte> output, SerializationValue value)
        {
            switch (value.Kind)
            {
                case ValueKind.Null: Byte(output, 0xc0); break;
                case ValueKind.Boolean: Byte(output, value.AsBoolean() ? (byte)0xc3 : (byte)0xc2); break;
                case ValueKind.Integer: WriteInteger(output, value.AsInt64()); break;
                case ValueKind.Number:
                    Byte(output, 0xcb);
                    BinaryPrimitives.WriteDoubleBigEndian(output.GetSpan(8), value.AsDouble());
                    output.Advance(8);
                    break;
                case ValueKind.Decimal:
                    // A number when a double holds it exactly; text otherwise, so no precision is lost.
                    var amount = value.AsDecimal();
                    var approximation = (double)amount;
                    if ((decimal)approximation == amount)
                        Write(output, SerializationValue.From(approximation));
                    else
                        WriteString(output, amount.ToString(CultureInfo.InvariantCulture));
                    break;
                case ValueKind.String: WriteString(output, value.AsString()); break;
                case ValueKind.Bytes:
                    var bytes = value.AsBytes();
                    WriteLength(output, bytes.Length, 0, 0xc4, 0xc5, 0xc6, fixLimit: 0);
                    output.Write(bytes);
                    break;
                case ValueKind.Timestamp: WriteTimestamp(output, value.AsTimestamp()); break;
                case ValueKind.Array:
                    WriteLength(output, value.Items.Count, 0x90, 0, 0xdc, 0xdd, fixLimit: 16);
                    foreach (var item in value.Items)
                        Write(output, item);
                    break;
                case ValueKind.Object:
                    WriteLength(output, value.Members.Count, 0x80, 0, 0xde, 0xdf, fixLimit: 16);
                    foreach (var (name, member) in value.Members)
                    {
                        WriteString(output, name);
                        Write(output, member);
                    }
                    break;
            }
        }

        private static void WriteInteger(IBufferWriter<byte> output, long value)
        {
            var span = output.GetSpan(9);
            int length;
            if (value >= 0)
            {
                if (value <= 0x7f) { span[0] = (byte)value; length = 1; }
                else if (value <= byte.MaxValue) { span[0] = 0xcc; span[1] = (byte)value; length = 2; }
                else if (value <= ushort.MaxValue) { span[0] = 0xcd; BinaryPrimitives.WriteUInt16BigEndian(span[1..], (ushort)value); length = 3; }
                else if (value <= uint.MaxValue) { span[0] = 0xce; BinaryPrimitives.WriteUInt32BigEndian(span[1..], (uint)value); length = 5; }
                else { span[0] = 0xcf; BinaryPrimitives.WriteUInt64BigEndian(span[1..], (ulong)value); length = 9; }
            }
            else
            {
                if (value >= -32) { span[0] = (byte)(sbyte)value; length = 1; }
                else if (value >= sbyte.MinValue) { span[0] = 0xd0; span[1] = (byte)(sbyte)value; length = 2; }
                else if (value >= short.MinValue) { span[0] = 0xd1; BinaryPrimitives.WriteInt16BigEndian(span[1..], (short)value); length = 3; }
                else if (value >= int.MinValue) { span[0] = 0xd2; BinaryPrimitives.WriteInt32BigEndian(span[1..], (int)value); length = 5; }
                else { span[0] = 0xd3; BinaryPrimitives.WriteInt64BigEndian(span[1..], value); length = 9; }
            }
            output.Advance(length);
        }

        private static void WriteString(IBufferWriter<byte> output, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            WriteLength(output, bytes.Length, 0xa0, 0xd9, 0xda, 0xdb, fixLimit: 32);
            output.Write(bytes);
        }

        // fix (when length < fixLimit), then 8-, 16- and 32-bit lengths (a zero code means the size is not available).
        private static void WriteLength(IBufferWriter<byte> output, int length, byte fix, byte code8, byte code16, byte code32, int fixLimit)
        {
            var span = output.GetSpan(5);
            if (length < fixLimit)
            {
                span[0] = (byte)(fix | length);
                output.Advance(1);
            }
            else if (code8 != 0 && length <= byte.MaxValue)
            {
                span[0] = code8;
                span[1] = (byte)length;
                output.Advance(2);
            }
            else if (length <= ushort.MaxValue)
            {
                span[0] = code16;
                BinaryPrimitives.WriteUInt16BigEndian(span[1..], (ushort)length);
                output.Advance(3);
            }
            else
            {
                span[0] = code32;
                BinaryPrimitives.WriteUInt32BigEndian(span[1..], (uint)length);
                output.Advance(5);
            }
        }

        // Timestamp extension: 32 bits (seconds), 64 bits (30-bit nanoseconds + 34-bit seconds) or 96 bits.
        private static void WriteTimestamp(IBufferWriter<byte> output, DateTimeOffset timestamp)
        {
            var ticks = timestamp.UtcTicks - DateTimeOffset.UnixEpoch.Ticks;
            var seconds = Math.DivRem(ticks, TimeSpan.TicksPerSecond, out var remainder);
            if (remainder < 0)
            {
                seconds--;
                remainder += TimeSpan.TicksPerSecond;
            }
            var nanoseconds = (uint)(remainder * 100);

            var span = output.GetSpan(15);
            if (seconds >> 34 == 0)
            {
                if (nanoseconds == 0 && seconds <= uint.MaxValue)
                {
                    span[0] = 0xd6;
                    span[1] = unchecked((byte)TimestampExtension);
                    BinaryPrimitives.WriteUInt32BigEndian(span[2..], (uint)seconds);
                    output.Advance(6);
                }
                else
                {
                    span[0] = 0xd7;
                    span[1] = unchecked((byte)TimestampExtension);
                    BinaryPrimitives.WriteUInt64BigEndian(span[2..], ((ulong)nanoseconds << 34) | (ulong)seconds);
                    output.Advance(10);
                }
            }
            else
            {
                span[0] = 0xc7;
                span[1] = 12;
                span[2] = unchecked((byte)TimestampExtension);
                BinaryPrimitives.WriteUInt32BigEndian(span[3..], nanoseconds);
                BinaryPrimitives.WriteInt64BigEndian(span[7..], seconds);
                output.Advance(15);
            }
        }

        private static void Byte(IBufferWriter<byte> output, byte value)
        {
            output.GetSpan(1)[0] = value;
            output.Advance(1);
        }

        private ref struct Reader(ReadOnlySpan<byte> data, int maxDepth)
        {
            private readonly ReadOnlySpan<byte> data = data;
            private int position;

            public readonly bool End => position >= data.Length;

            public SerializationValue Read(int depth)
            {
                if (depth > maxDepth)
                    throw new SerializationException("", $"nested deeper than {maxDepth} levels.");

                var code = Take(1)[0];
                switch (code)
                {
                    case <= 0x7f: return SerializationValue.From((long)code);
                    case >= 0xe0: return SerializationValue.From((long)(sbyte)code);
                    case >= 0x80 and <= 0x8f: return Map(code & 0x0f, depth);
                    case >= 0x90 and <= 0x9f: return Array(code & 0x0f, depth);
                    case >= 0xa0 and <= 0xbf: return Text(code & 0x1f);
                    case 0xc0: return SerializationValue.Null;
                    case 0xc2: return SerializationValue.False;
                    case 0xc3: return SerializationValue.True;
                    case 0xc4: return SerializationValue.From(Take(Take(1)[0]).ToArray());
                    case 0xc5: return SerializationValue.From(Take(BinaryPrimitives.ReadUInt16BigEndian(Take(2))).ToArray());
                    case 0xc6: return SerializationValue.From(Take(Length(BinaryPrimitives.ReadUInt32BigEndian(Take(4)))).ToArray());
                    case 0xc7: return Extension(Take(1)[0]);
                    case 0xc8: return Extension(BinaryPrimitives.ReadUInt16BigEndian(Take(2)));
                    case 0xc9: return Extension(Length(BinaryPrimitives.ReadUInt32BigEndian(Take(4))));
                    case 0xca: return SerializationValue.From((double)BinaryPrimitives.ReadSingleBigEndian(Take(4)));
                    case 0xcb: return SerializationValue.From(BinaryPrimitives.ReadDoubleBigEndian(Take(8)));
                    case 0xcc: return SerializationValue.From((long)Take(1)[0]);
                    case 0xcd: return SerializationValue.From((long)BinaryPrimitives.ReadUInt16BigEndian(Take(2)));
                    case 0xce: return SerializationValue.From((long)BinaryPrimitives.ReadUInt32BigEndian(Take(4)));
                    case 0xcf:
                        var unsigned = BinaryPrimitives.ReadUInt64BigEndian(Take(8));
                        return unsigned <= long.MaxValue ? SerializationValue.From((long)unsigned) : SerializationValue.From((decimal)unsigned);
                    case 0xd0: return SerializationValue.From((long)(sbyte)Take(1)[0]);
                    case 0xd1: return SerializationValue.From((long)BinaryPrimitives.ReadInt16BigEndian(Take(2)));
                    case 0xd2: return SerializationValue.From((long)BinaryPrimitives.ReadInt32BigEndian(Take(4)));
                    case 0xd3: return SerializationValue.From(BinaryPrimitives.ReadInt64BigEndian(Take(8)));
                    case 0xd4: return Extension(1);
                    case 0xd5: return Extension(2);
                    case 0xd6: return Extension(4);
                    case 0xd7: return Extension(8);
                    case 0xd8: return Extension(16);
                    case 0xd9: return Text(Take(1)[0]);
                    case 0xda: return Text(BinaryPrimitives.ReadUInt16BigEndian(Take(2)));
                    case 0xdb: return Text(Length(BinaryPrimitives.ReadUInt32BigEndian(Take(4))));
                    case 0xdc: return Array(BinaryPrimitives.ReadUInt16BigEndian(Take(2)), depth);
                    case 0xdd: return Array(Length(BinaryPrimitives.ReadUInt32BigEndian(Take(4))), depth);
                    case 0xde: return Map(BinaryPrimitives.ReadUInt16BigEndian(Take(2)), depth);
                    case 0xdf: return Map(Length(BinaryPrimitives.ReadUInt32BigEndian(Take(4))), depth);
                    default: throw new SerializationException("", $"invalid MessagePack code 0x{code:x2}.");
                }
            }

            private SerializationValue Array(int count, int depth)
            {
                // Each item takes at least one byte: a larger count is a lie that would allocate without limit.
                if (count > data.Length - position)
                    throw Truncated();
                var items = new SerializationValue[count];
                for (var i = 0; i < count; i++)
                    items[i] = Read(depth + 1);
                return SerializationValue.Array(items);
            }

            private SerializationValue Map(int count, int depth)
            {
                if (count > (data.Length - position) / 2)
                    throw Truncated();
                var members = new KeyValuePair<string, SerializationValue>[count];
                for (var i = 0; i < count; i++)
                {
                    var key = Read(depth + 1);
                    var name = key.Kind switch
                    {
                        ValueKind.String => key.AsString(),
                        ValueKind.Integer => key.AsInt64().ToString(CultureInfo.InvariantCulture),
                        _ => throw new SerializationException("", $"map keys must be strings or integers, not {key.Kind}.")
                    };
                    members[i] = new(name, Read(depth + 1));
                }
                return SerializationValue.Object(members);
            }

            private SerializationValue Text(int length)
            {
                try
                {
                    return SerializationValue.From(new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(Take(length)));
                }
                catch (DecoderFallbackException)
                {
                    throw new SerializationException("", "a string is not valid UTF-8.");
                }
            }

            private SerializationValue Extension(int length)
            {
                var type = (sbyte)Take(1)[0];
                var payload = Take(length);
                if (type != TimestampExtension)
                    return SerializationValue.From(payload.ToArray());

                long seconds;
                long nanoseconds;
                switch (length)
                {
                    case 4:
                        seconds = BinaryPrimitives.ReadUInt32BigEndian(payload);
                        nanoseconds = 0;
                        break;
                    case 8:
                        var combined = BinaryPrimitives.ReadUInt64BigEndian(payload);
                        nanoseconds = (long)(combined >> 34);
                        seconds = (long)(combined & 0x3_FFFF_FFFF);
                        break;
                    case 12:
                        nanoseconds = BinaryPrimitives.ReadUInt32BigEndian(payload);
                        seconds = BinaryPrimitives.ReadInt64BigEndian(payload[4..]);
                        break;
                    default:
                        throw new SerializationException("", $"invalid timestamp extension of {length} bytes.");
                }
                try
                {
                    return SerializationValue.From(DateTimeOffset.UnixEpoch.AddSeconds(seconds).AddTicks(nanoseconds / 100));
                }
                catch (ArgumentOutOfRangeException)
                {
                    throw new SerializationException("", "the timestamp is out of range.");
                }
            }

            private ReadOnlySpan<byte> Take(int length)
            {
                if (length < 0 || data.Length - position < length)
                    throw Truncated();
                var span = data.Slice(position, length);
                position += length;
                return span;
            }

            private static int Length(uint length) => length > int.MaxValue ? throw new SerializationException("", "a length is too large.") : (int)length;

            private static SerializationException Truncated() => new("", "the MessagePack data is truncated.");
        }
    }
}

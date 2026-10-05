using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Butterfly.Serialization.Cbor
{
    /// <summary>
    /// CBOR (RFC 8949). Objects are maps keyed by member name (camelCase by default), enums are numbers by default,
    /// integers use the shortest encoding, timestamps are tag 0 (RFC 3339 text, keeping the offset), decimals are tag 4
    /// (decimal fraction) and bytes are byte strings. Reading accepts definite and indefinite lengths, half/single/double
    /// floats, epoch timestamps (tag 1) and bignums (tags 2 and 3).
    /// </summary>
    public sealed class CborFormat : ValueFormat
    {
        private const int MajorUnsigned = 0, MajorNegative = 1, MajorBytes = 2, MajorText = 3, MajorArray = 4, MajorMap = 5, MajorTag = 6, MajorSimple = 7;

        public static CborFormat Instance { get; } = new();

        public override string Name => "cbor";
        public override string MediaType => "application/cbor";
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
                case ValueKind.Null: Byte(output, 0xf6); break;
                case ValueKind.Boolean: Byte(output, value.AsBoolean() ? (byte)0xf5 : (byte)0xf4); break;
                case ValueKind.Integer: WriteInteger(output, value.AsInt64()); break;
                case ValueKind.Number:
                    Byte(output, 0xfb);
                    BinaryPrimitives.WriteDoubleBigEndian(output.GetSpan(8), value.AsDouble());
                    output.Advance(8);
                    break;
                case ValueKind.Decimal: WriteDecimal(output, value.AsDecimal()); break;
                case ValueKind.String: WriteText(output, value.AsString()); break;
                case ValueKind.Bytes:
                    var bytes = value.AsBytes();
                    WriteHead(output, MajorBytes, (ulong)bytes.Length);
                    output.Write(bytes);
                    break;
                case ValueKind.Timestamp:
                    WriteHead(output, MajorTag, 0);
                    WriteText(output, ScalarText.FormatTimestamp(value.AsTimestamp()));
                    break;
                case ValueKind.Array:
                    WriteHead(output, MajorArray, (ulong)value.Items.Count);
                    foreach (var item in value.Items)
                        Write(output, item);
                    break;
                case ValueKind.Object:
                    WriteHead(output, MajorMap, (ulong)value.Members.Count);
                    foreach (var (name, member) in value.Members)
                    {
                        WriteText(output, name);
                        Write(output, member);
                    }
                    break;
            }
        }

        private static void WriteInteger(IBufferWriter<byte> output, long value)
        {
            if (value >= 0)
                WriteHead(output, MajorUnsigned, (ulong)value);
            else
                WriteHead(output, MajorNegative, (ulong)(-1 - value));
        }

        // Tag 4: [exponent, mantissa], with a bignum mantissa when it does not fit in 64 bits.
        private static void WriteDecimal(IBufferWriter<byte> output, decimal value)
        {
            var bits = decimal.GetBits(value);
            var scale = (bits[3] >> 16) & 0xff;
            var negative = bits[3] < 0;
            var magnitude = new BigInteger((uint)bits[0]) | (new BigInteger((uint)bits[1]) << 32) | (new BigInteger((uint)bits[2]) << 64);

            WriteHead(output, MajorTag, 4);
            WriteHead(output, MajorArray, 2);
            WriteInteger(output, -scale);
            if (magnitude <= long.MaxValue)
            {
                WriteInteger(output, negative ? -(long)magnitude : (long)magnitude);
            }
            else
            {
                // Tag 2 is n, tag 3 is -1 - n.
                var encoded = negative ? magnitude - 1 : magnitude;
                WriteHead(output, MajorTag, negative ? 3UL : 2UL);
                var bytes = encoded.ToByteArray(isUnsigned: true, isBigEndian: true);
                WriteHead(output, MajorBytes, (ulong)bytes.Length);
                output.Write(bytes);
            }
        }

        private static void WriteText(IBufferWriter<byte> output, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            WriteHead(output, MajorText, (ulong)bytes.Length);
            output.Write(bytes);
        }

        // Initial byte with the major type and the shortest argument encoding.
        private static void WriteHead(IBufferWriter<byte> output, int major, ulong argument)
        {
            var span = output.GetSpan(9);
            var type = (byte)(major << 5);
            if (argument < 24) { span[0] = (byte)(type | (byte)argument); output.Advance(1); }
            else if (argument <= byte.MaxValue) { span[0] = (byte)(type | 24); span[1] = (byte)argument; output.Advance(2); }
            else if (argument <= ushort.MaxValue) { span[0] = (byte)(type | 25); BinaryPrimitives.WriteUInt16BigEndian(span[1..], (ushort)argument); output.Advance(3); }
            else if (argument <= uint.MaxValue) { span[0] = (byte)(type | 26); BinaryPrimitives.WriteUInt32BigEndian(span[1..], (uint)argument); output.Advance(5); }
            else { span[0] = (byte)(type | 27); BinaryPrimitives.WriteUInt64BigEndian(span[1..], argument); output.Advance(9); }
        }

        private static void Byte(IBufferWriter<byte> output, byte value)
        {
            output.GetSpan(1)[0] = value;
            output.Advance(1);
        }

        private ref struct Reader(ReadOnlySpan<byte> data, int maxDepth)
        {
            private const ulong Indefinite = ulong.MaxValue;
            private readonly ReadOnlySpan<byte> data = data;
            private int position;

            public readonly bool End => position >= data.Length;

            public SerializationValue Read(int depth)
            {
                if (depth > maxDepth)
                    throw new SerializationException("", $"nested deeper than {maxDepth} levels.");

                var initial = Take(1)[0];
                var major = initial >> 5;
                var info = initial & 0x1f;

                if (major == MajorSimple)
                    return Simple(info);

                var argument = Argument(info, allowIndefinite: major is MajorBytes or MajorText or MajorArray or MajorMap);
                switch (major)
                {
                    case MajorUnsigned:
                        return argument <= long.MaxValue ? SerializationValue.From((long)argument) : SerializationValue.From((decimal)argument);
                    case MajorNegative:
                        return argument < long.MaxValue ? SerializationValue.From(-1 - (long)argument) : SerializationValue.From(-1m - argument);
                    case MajorBytes:
                        return SerializationValue.From(argument == Indefinite ? Chunks(MajorBytes) : Take(Length(argument)).ToArray());
                    case MajorText:
                        return SerializationValue.From(Utf8(argument == Indefinite ? Chunks(MajorText) : Take(Length(argument))));
                    case MajorArray:
                        var items = new List<SerializationValue>();
                        if (argument == Indefinite)
                        {
                            while (!Break())
                                items.Add(Read(depth + 1));
                        }
                        else
                        {
                            CheckCount(argument, 1);
                            for (ulong i = 0; i < argument; i++)
                                items.Add(Read(depth + 1));
                        }
                        return SerializationValue.Array(items);
                    case MajorMap:
                        var members = new List<KeyValuePair<string, SerializationValue>>();
                        if (argument == Indefinite)
                        {
                            while (!Break())
                                members.Add(Entry(depth));
                        }
                        else
                        {
                            CheckCount(argument, 2);
                            for (ulong i = 0; i < argument; i++)
                                members.Add(Entry(depth));
                        }
                        return SerializationValue.Object(members);
                    default:
                        return Tagged(argument, depth);
                }
            }

            private SerializationValue Tagged(ulong tag, int depth)
            {
                var content = Read(depth + 1);
                switch (tag)
                {
                    case 0 when content.Kind == ValueKind.String:
                        return DateTimeOffset.TryParse(content.AsString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
                            ? SerializationValue.From(date)
                            : throw new SerializationException("", "invalid tag 0 date.");
                    case 1 when content.IsNumeric:
                        try
                        {
                            return SerializationValue.From(DateTimeOffset.UnixEpoch.AddTicks((long)Math.Round(content.AsDouble() * TimeSpan.TicksPerSecond)));
                        }
                        catch (ArgumentOutOfRangeException)
                        {
                            throw new SerializationException("", "the timestamp is out of range.");
                        }
                    case 2 or 3 when content.Kind == ValueKind.Bytes:
                        var magnitude = new BigInteger(content.AsBytes(), isUnsigned: true, isBigEndian: true);
                        var integer = tag == 2 ? magnitude : -1 - magnitude;
                        return integer >= long.MinValue && integer <= long.MaxValue ? SerializationValue.From((long)integer)
                            : BigInteger.Abs(integer) <= new BigInteger(decimal.MaxValue) ? SerializationValue.From((decimal)integer)
                            : SerializationValue.From((double)integer);
                    case 4 when content.Kind == ValueKind.Array && content.Items.Count == 2:
                        try
                        {
                            var exponent = content[0].AsInt64();
                            // decimal has at most 28 digits: a larger exponent would loop for nothing (a denial of service).
                            if (Math.Abs(exponent) > 28)
                                throw new OverflowException();
                            var mantissa = content[1].AsDecimal();
                            var result = mantissa;
                            for (var i = 0L; i < Math.Abs(exponent); i++)
                                result = exponent < 0 ? result / 10 : result * 10;
                            return SerializationValue.From(result);
                        }
                        catch (Exception exception) when (exception is OverflowException or InvalidOperationException)
                        {
                            throw new SerializationException("", "the decimal fraction is out of range.");
                        }
                    default:
                        // Unknown tags (URIs, self-describe...) keep their content.
                        return content;
                }
            }

            private KeyValuePair<string, SerializationValue> Entry(int depth)
            {
                var key = Read(depth + 1);
                var name = key.Kind switch
                {
                    ValueKind.String => key.AsString(),
                    ValueKind.Integer => key.AsInt64().ToString(CultureInfo.InvariantCulture),
                    _ => throw new SerializationException("", $"map keys must be text or integers, not {key.Kind}.")
                };
                return new(name, Read(depth + 1));
            }

            private SerializationValue Simple(int info)
            {
                switch (info)
                {
                    case 20: return SerializationValue.False;
                    case 21: return SerializationValue.True;
                    case 22 or 23: return SerializationValue.Null;   // null, undefined
                    case 25: return SerializationValue.From((double)BinaryPrimitives.ReadHalfBigEndian(Take(2)));
                    case 26: return SerializationValue.From((double)BinaryPrimitives.ReadSingleBigEndian(Take(4)));
                    case 27: return SerializationValue.From(BinaryPrimitives.ReadDoubleBigEndian(Take(8)));
                    case 24: Take(1); return SerializationValue.Null;
                    default:
                        if (info < 20)
                            return SerializationValue.Null;
                        throw new SerializationException("", $"invalid simple value {info}.");
                }
            }

            private ulong Argument(int info, bool allowIndefinite) => info switch
            {
                < 24 => (ulong)info,
                24 => Take(1)[0],
                25 => BinaryPrimitives.ReadUInt16BigEndian(Take(2)),
                26 => BinaryPrimitives.ReadUInt32BigEndian(Take(4)),
                27 => BinaryPrimitives.ReadUInt64BigEndian(Take(8)),
                31 when allowIndefinite => Indefinite,
                _ => throw new SerializationException("", $"invalid additional information {info}.")
            };

            // Indefinite-length strings are definite chunks of the same major type, ended by a break.
            private byte[] Chunks(int major)
            {
                var result = new List<byte>();
                while (!Break())
                {
                    var initial = Take(1)[0];
                    if (initial >> 5 != major)
                        throw new SerializationException("", "an indefinite string contains a chunk of another type.");
                    var length = Argument(initial & 0x1f, allowIndefinite: false);
                    result.AddRange(Take(Length(length)).ToArray());
                }
                return [.. result];
            }

            private bool Break()
            {
                if (position >= data.Length)
                    throw Truncated();
                if (data[position] != 0xff)
                    return false;
                position++;
                return true;
            }

            private readonly void CheckCount(ulong count, int minimumBytesEach)
            {
                if (count > (ulong)((data.Length - position) / minimumBytesEach))
                    throw Truncated();
            }

            private static string Utf8(ReadOnlySpan<byte> bytes)
            {
                try
                {
                    return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
                }
                catch (DecoderFallbackException)
                {
                    throw new SerializationException("", "a text string is not valid UTF-8.");
                }
            }

            private ReadOnlySpan<byte> Take(int length)
            {
                if (data.Length - position < length)
                    throw Truncated();
                var span = data.Slice(position, length);
                position += length;
                return span;
            }

            private static int Length(ulong length) => length > int.MaxValue ? throw new SerializationException("", "a length is too large.") : (int)length;

            private static SerializationException Truncated() => new("", "the CBOR data is truncated.");
        }
    }
}

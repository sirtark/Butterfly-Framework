using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Butterfly.Serialization.Protobuf
{
    /// <summary>
    /// Protocol Buffers (proto3), wire-compatible with protoc-generated code. Mapping: bool, int32, int64 and enums are
    /// varints, double is fixed64, string and bytes are length-delimited, timestamps are google.protobuf.Timestamp,
    /// dictionaries are map&lt;string, T&gt;, dynamic values are google.protobuf.Value, and decimal and Guid travel as strings
    /// (protobuf has no such types). Defaults of non-optional members are not written; repeated numbers are packed and read
    /// in both forms. A root that is not an object is wrapped in a message whose field 1 is the value.
    /// </summary>
    public sealed class ProtobufFormat : SerializationFormat
    {
        private const int Varint = 0, Fixed64 = 1, LengthDelimited = 2, Fixed32 = 5;

        public static ProtobufFormat Instance { get; } = new();

        public override string Name => "protobuf";
        public override string MediaType => "application/x-protobuf";
        public override bool IsText => false;

        public override void Write(IBufferWriter<byte> output, SerializableType type, object? value, SerializationProfile profile)
        {
            if (type is ObjectType objectType)
            {
                if (value is not null)
                    WriteMessage(output, objectType, value, profile, 0);
                return;
            }
            WriteValueField(output, 1, type, value, optional: true, profile, 0);
        }

        public override object? Read(ReadOnlyMemory<byte> input, SerializableType type, SerializationProfile profile)
        {
            if (type is ObjectType objectType)
                return ReadMessage(objectType, input.Span, profile, "", 0);

            // The wrapper message: field 1 holds the value.
            var wrapper = ObjectType.CreateArrayBacked("Wrapper", "", [("value", 1, type, true)]);
            return ((object?[])ReadMessage(wrapper, input.Span, profile, "", 0))[0] ?? type.CreateDefault();
        }

        // ------------------------------------------------------------------ writing

        private static void WriteMessage(IBufferWriter<byte> output, ObjectType type, object message, SerializationProfile profile, int depth)
        {
            if (depth > profile.MaxDepth)
                throw new SerializationException("", $"the value is nested deeper than {profile.MaxDepth} levels.");

            foreach (var member in type.Members)
            {
                if (member.IsVisibleIn(profile))
                    WriteValueField(output, member.Number, member.Type, member.Get(message), member.IsOptional, profile, depth);
            }
        }

        private static void WriteValueField(IBufferWriter<byte> output, int number, SerializableType type, object? value, bool optional, SerializationProfile profile, int depth)
        {
            if (value is null)
                return;

            switch (type)
            {
                case ListType list:
                    WriteList(output, number, list, value, profile, depth);
                    break;
                case MapType map:
                    foreach (var (key, entryValue) in map.Enumerate(value))
                    {
                        var entry = new ArrayBufferWriter<byte>();
                        WriteBytes(entry, 1, Encoding.UTF8.GetBytes(key));
                        WriteValueField(entry, 2, map.Value, entryValue, optional: true, profile, depth + 1);
                        WriteBytes(output, number, entry.WrittenSpan);
                    }
                    break;
                default:
                    if (optional || !ScalarText.IsDefault(type, value))
                        WriteField(output, number, type, value, profile, depth);
                    break;
            }
        }

        private static void WriteList(IBufferWriter<byte> output, int number, ListType list, object value, SerializationProfile profile, int depth)
        {
            var items = list.Enumerate(value).ToList();
            if (items.Count == 0)
                return;

            if (IsPackable(list.Element))
            {
                var packed = new ArrayBufferWriter<byte>();
                foreach (var item in items)
                    WriteScalar(packed, list.Element, item ?? list.Element.CreateDefault()!);
                WriteTag(output, number, LengthDelimited);
                WriteVarint(output, (ulong)packed.WrittenCount);
                output.Write(packed.WrittenSpan);
                return;
            }

            foreach (var item in items)
            {
                if (item is null && list.Element is ObjectType)
                    throw new SerializationException("", "lists of objects cannot contain null items in Protocol Buffers.");
                WriteField(output, number, list.Element, item ?? list.Element.CreateDefault(), profile, depth);
            }
        }

        private static void WriteField(IBufferWriter<byte> output, int number, SerializableType type, object? value, SerializationProfile profile, int depth)
        {
            switch (type.Kind)
            {
                case TypeKind.Boolean or TypeKind.Int32 or TypeKind.Int64 or TypeKind.Enum:
                    WriteTag(output, number, Varint);
                    WriteScalar(output, type, value!);
                    break;
                case TypeKind.Double:
                    WriteTag(output, number, Fixed64);
                    WriteScalar(output, type, value!);
                    break;
                case TypeKind.String or TypeKind.Decimal or TypeKind.Guid:
                    WriteBytes(output, number, Encoding.UTF8.GetBytes(ScalarText.Format(type, value!)));
                    break;
                case TypeKind.Bytes:
                    WriteBytes(output, number, (byte[])value!);
                    break;
                case TypeKind.Timestamp:
                    WriteBytes(output, number, Timestamp(ScalarText.ToTimestamp(value!)));
                    break;
                case TypeKind.Duration:
                    WriteBytes(output, number, Duration((TimeSpan)value!));
                    break;
                case TypeKind.Object:
                    var nested = new ArrayBufferWriter<byte>();
                    if (value is not null)
                        WriteMessage(nested, (ObjectType)type, value, profile, depth + 1);
                    WriteBytes(output, number, nested.WrittenSpan);
                    break;
                case TypeKind.Dynamic:
                    WriteBytes(output, number, GoogleValue(((DynamicType)type).ToValue(value), profile, depth + 1));
                    break;
                default:
                    throw new SerializationException("", $"nested collections cannot be encoded in Protocol Buffers ({type}).");
            }
        }

        // google.protobuf.Timestamp { int64 seconds = 1; int32 nanos = 2; }
        private static byte[] Timestamp(DateTimeOffset timestamp)
        {
            var ticks = timestamp.UtcTicks - DateTimeOffset.UnixEpoch.Ticks;
            var seconds = Math.DivRem(ticks, TimeSpan.TicksPerSecond, out var remainder);
            if (remainder < 0)
            {
                seconds--;
                remainder += TimeSpan.TicksPerSecond;
            }
            var output = new ArrayBufferWriter<byte>();
            if (seconds != 0)
            {
                WriteTag(output, 1, Varint);
                WriteVarint(output, (ulong)seconds);
            }
            if (remainder != 0)
            {
                WriteTag(output, 2, Varint);
                WriteVarint(output, (ulong)(remainder * 100));
            }
            return output.WrittenSpan.ToArray();
        }

        // google.protobuf.Duration { int64 seconds = 1; int32 nanos = 2; }: both carry the sign of the duration.
        private static byte[] Duration(TimeSpan duration)
        {
            var seconds = Math.DivRem(duration.Ticks, TimeSpan.TicksPerSecond, out var remainder);
            var output = new ArrayBufferWriter<byte>();
            if (seconds != 0)
            {
                WriteTag(output, 1, Varint);
                WriteVarint(output, (ulong)seconds);
            }
            if (remainder != 0)
            {
                WriteTag(output, 2, Varint);
                WriteVarint(output, (ulong)(long)(remainder * 100));
            }
            return output.WrittenSpan.ToArray();
        }

        // google.protobuf.Value { oneof kind { NullValue null_value = 1; double number_value = 2; string string_value = 3;
        //                                      bool bool_value = 4; Struct struct_value = 5; ListValue list_value = 6; } }
        private static byte[] GoogleValue(SerializationValue value, SerializationProfile profile, int depth)
        {
            if (depth > profile.MaxDepth)
                throw new SerializationException("", $"the value is nested deeper than {profile.MaxDepth} levels.");

            var output = new ArrayBufferWriter<byte>();
            switch (value.Kind)
            {
                case ValueKind.Null:
                    WriteTag(output, 1, Varint);
                    WriteVarint(output, 0);
                    break;
                case ValueKind.Boolean:
                    WriteTag(output, 4, Varint);
                    WriteVarint(output, value.AsBoolean() ? 1UL : 0UL);
                    break;
                case ValueKind.Integer or ValueKind.Number or ValueKind.Decimal:
                    WriteTag(output, 2, Fixed64);
                    BinaryPrimitives.WriteDoubleLittleEndian(output.GetSpan(8), value.AsDouble());
                    output.Advance(8);
                    break;
                case ValueKind.String:
                    WriteBytes(output, 3, Encoding.UTF8.GetBytes(value.AsString()));
                    break;
                case ValueKind.Bytes:
                    WriteBytes(output, 3, Encoding.UTF8.GetBytes(Convert.ToBase64String(value.AsBytes())));
                    break;
                case ValueKind.Timestamp:
                    WriteBytes(output, 3, Encoding.UTF8.GetBytes(ScalarText.FormatTimestamp(value.AsTimestamp())));
                    break;
                case ValueKind.Array:
                    var list = new ArrayBufferWriter<byte>();
                    foreach (var item in value.Items)
                        WriteBytes(list, 1, GoogleValue(item, profile, depth + 1));
                    WriteBytes(output, 6, list.WrittenSpan);
                    break;
                case ValueKind.Object:
                    // Struct { map<string, Value> fields = 1; }
                    var fields = new ArrayBufferWriter<byte>();
                    foreach (var (name, member) in value.Members)
                    {
                        var entry = new ArrayBufferWriter<byte>();
                        WriteBytes(entry, 1, Encoding.UTF8.GetBytes(name));
                        WriteBytes(entry, 2, GoogleValue(member, profile, depth + 1));
                        WriteBytes(fields, 1, entry.WrittenSpan);
                    }
                    WriteBytes(output, 5, fields.WrittenSpan);
                    break;
            }
            return output.WrittenSpan.ToArray();
        }

        // Value of a varint or fixed64 field, without its tag (also the element of a packed list).
        private static void WriteScalar(IBufferWriter<byte> output, SerializableType type, object value)
        {
            switch (type.Kind)
            {
                case TypeKind.Boolean: WriteVarint(output, (bool)value ? 1UL : 0UL); break;
                // Negative int32 values are sign-extended to 64 bits, as protobuf requires.
                case TypeKind.Int32: WriteVarint(output, (ulong)(long)(int)value); break;
                case TypeKind.Int64: WriteVarint(output, (ulong)(long)value); break;
                case TypeKind.Enum: WriteVarint(output, (ulong)((EnumType)type).ToNumber(value)); break;
                case TypeKind.Double:
                    BinaryPrimitives.WriteDoubleLittleEndian(output.GetSpan(8), (double)value);
                    output.Advance(8);
                    break;
            }
        }

        // ------------------------------------------------------------------ reading

        private static object ReadMessage(ObjectType type, ReadOnlySpan<byte> data, SerializationProfile profile, string path, int depth)
        {
            if (depth > profile.MaxDepth)
                throw new SerializationException(path, $"nested deeper than {profile.MaxDepth} levels.");

            var values = type.CreateDefaultValues();
            List<object?>?[]? lists = null;
            List<KeyValuePair<string, object?>>?[]? maps = null;
            var reader = new Reader(data, path);

            while (!reader.End)
            {
                var (number, wireType) = reader.ReadTag();
                var member = type.FindMember(number);
                if (member is null || !member.IsVisibleIn(profile))
                {
                    if (member is null && profile.RejectUnknownMembers)
                        throw new SerializationException(path, $"unknown field {number}.");
                    reader.Skip(wireType);
                    continue;
                }

                var memberPath = SerializationException.Child(path, member.Name);
                switch (member.Type)
                {
                    case ListType list:
                        lists ??= new List<object?>?[type.Members.Count];
                        var items = lists[member.Index] ??= [];
                        if (wireType == LengthDelimited && IsPackable(list.Element))
                        {
                            var packed = new Reader(reader.ReadLengthDelimited(), memberPath);
                            while (!packed.End)
                                items.Add(ReadScalar(ref packed, list.Element, list.Element.Kind == TypeKind.Double ? Fixed64 : Varint, memberPath));
                        }
                        else
                        {
                            items.Add(ReadField(ref reader, list.Element, wireType, profile, memberPath, depth) ?? list.Element.CreateDefault());
                        }
                        break;
                    case MapType map:
                        Expect(wireType, LengthDelimited, memberPath);
                        maps ??= new List<KeyValuePair<string, object?>>?[type.Members.Count];
                        (maps[member.Index] ??= []).Add(ReadMapEntry(map, reader.ReadLengthDelimited(), profile, memberPath, depth));
                        break;
                    default:
                        values[member.Index] = ReadField(ref reader, member.Type, wireType, profile, memberPath, depth);
                        break;
                }
            }

            foreach (var member in type.Members)
            {
                if (lists?[member.Index] is { } items)
                    values[member.Index] = ((ListType)member.Type).Create(items);
                else if (maps?[member.Index] is { } entries)
                    values[member.Index] = ((MapType)member.Type).Create(entries);
            }
            return type.Create(values);
        }

        private static KeyValuePair<string, object?> ReadMapEntry(MapType map, ReadOnlySpan<byte> data, SerializationProfile profile, string path, int depth)
        {
            var reader = new Reader(data, path);
            var key = "";
            object? value = null;
            while (!reader.End)
            {
                var (number, wireType) = reader.ReadTag();
                if (number == 1)
                {
                    Expect(wireType, LengthDelimited, path);
                    key = DecodeUtf8(reader.ReadLengthDelimited(), path);
                }
                else if (number == 2)
                {
                    value = ReadField(ref reader, map.Value, wireType, profile, SerializationException.Child(path, key), depth + 1);
                }
                else
                {
                    reader.Skip(wireType);
                }
            }
            return new(key, value ?? (map.ValueIsOptional ? null : map.Value.CreateDefault()));
        }

        private static object? ReadField(ref Reader reader, SerializableType type, int wireType, SerializationProfile profile, string path, int depth)
        {
            switch (type.Kind)
            {
                case TypeKind.Boolean or TypeKind.Int32 or TypeKind.Int64 or TypeKind.Enum or TypeKind.Double:
                    return ReadScalar(ref reader, type, wireType, path);
                case TypeKind.String or TypeKind.Decimal or TypeKind.Guid:
                    Expect(wireType, LengthDelimited, path);
                    var text = DecodeUtf8(reader.ReadLengthDelimited(), path);
                    if (type.Kind == TypeKind.String)
                        return text;
                    return ScalarText.TryParse(type, text, out var parsed) ? parsed : throw new SerializationException(path, $"'{text}' is not a valid {type}.");
                case TypeKind.Bytes:
                    Expect(wireType, LengthDelimited, path);
                    return reader.ReadLengthDelimited().ToArray();
                case TypeKind.Timestamp:
                    Expect(wireType, LengthDelimited, path);
                    var timestamp = new Reader(reader.ReadLengthDelimited(), path);
                    long seconds = 0, nanos = 0;
                    while (!timestamp.End)
                    {
                        var (number, innerWire) = timestamp.ReadTag();
                        if (number == 1 && innerWire == Varint) seconds = (long)timestamp.ReadVarint();
                        else if (number == 2 && innerWire == Varint) nanos = (int)timestamp.ReadVarint();
                        else timestamp.Skip(innerWire);
                    }
                    try
                    {
                        return ScalarText.FromTimestamp(type, DateTimeOffset.UnixEpoch.AddSeconds(seconds).AddTicks(nanos / 100));
                    }
                    catch (ArgumentOutOfRangeException)
                    {
                        throw new SerializationException(path, "the timestamp is out of range.");
                    }
                case TypeKind.Duration:
                    Expect(wireType, LengthDelimited, path);
                    var duration = new Reader(reader.ReadLengthDelimited(), path);
                    long durationSeconds = 0, durationNanos = 0;
                    while (!duration.End)
                    {
                        var (number, innerWire) = duration.ReadTag();
                        if (number == 1 && innerWire == Varint) durationSeconds = (long)duration.ReadVarint();
                        else if (number == 2 && innerWire == Varint) durationNanos = (int)duration.ReadVarint();
                        else duration.Skip(innerWire);
                    }
                    try
                    {
                        return TimeSpan.FromTicks(checked(durationSeconds * TimeSpan.TicksPerSecond + durationNanos / 100));
                    }
                    catch (OverflowException)
                    {
                        throw new SerializationException(path, "the duration is out of range.");
                    }
                case TypeKind.Object:
                    Expect(wireType, LengthDelimited, path);
                    return ReadMessage((ObjectType)type, reader.ReadLengthDelimited(), profile, path, depth + 1);
                case TypeKind.Dynamic:
                    Expect(wireType, LengthDelimited, path);
                    return ((DynamicType)type).FromValue(ReadGoogleValue(reader.ReadLengthDelimited(), profile, path, depth + 1));
                default:
                    throw new SerializationException(path, $"nested collections are not supported ({type}).");
            }
        }

        private static SerializationValue ReadGoogleValue(ReadOnlySpan<byte> data, SerializationProfile profile, string path, int depth)
        {
            if (depth > profile.MaxDepth)
                throw new SerializationException(path, $"nested deeper than {profile.MaxDepth} levels.");

            var reader = new Reader(data, path);
            var value = SerializationValue.Null;
            while (!reader.End)
            {
                var (number, wireType) = reader.ReadTag();
                switch (number)
                {
                    case 1: reader.ReadVarint(); value = SerializationValue.Null; break;
                    case 2:
                        Expect(wireType, Fixed64, path);
                        var number2 = BinaryPrimitives.ReadDoubleLittleEndian(reader.ReadFixed(8));
                        // Whole numbers come back as integers, the common case for JSON-like data.
                        value = double.IsInteger(number2) && Math.Abs(number2) < 9e15 ? SerializationValue.From((long)number2) : SerializationValue.From(number2);
                        break;
                    case 3: value = SerializationValue.From(DecodeUtf8(reader.ReadLengthDelimited(), path)); break;
                    case 4: value = SerializationValue.From(reader.ReadVarint() != 0); break;
                    case 5:
                        var members = new List<KeyValuePair<string, SerializationValue>>();
                        var fields = new Reader(reader.ReadLengthDelimited(), path);
                        while (!fields.End)
                        {
                            var (fieldNumber, fieldWire) = fields.ReadTag();
                            if (fieldNumber != 1)
                            {
                                fields.Skip(fieldWire);
                                continue;
                            }
                            var entry = new Reader(fields.ReadLengthDelimited(), path);
                            var key = "";
                            var entryValue = SerializationValue.Null;
                            while (!entry.End)
                            {
                                var (entryNumber, entryWire) = entry.ReadTag();
                                if (entryNumber == 1) key = DecodeUtf8(entry.ReadLengthDelimited(), path);
                                else if (entryNumber == 2) entryValue = ReadGoogleValue(entry.ReadLengthDelimited(), profile, SerializationException.Child(path, key), depth + 1);
                                else entry.Skip(entryWire);
                            }
                            members.Add(new(key, entryValue));
                        }
                        value = SerializationValue.Object(members);
                        break;
                    case 6:
                        var items = new List<SerializationValue>();
                        var list = new Reader(reader.ReadLengthDelimited(), path);
                        while (!list.End)
                        {
                            var (itemNumber, itemWire) = list.ReadTag();
                            if (itemNumber == 1) items.Add(ReadGoogleValue(list.ReadLengthDelimited(), profile, SerializationException.Item(path, items.Count), depth + 1));
                            else list.Skip(itemWire);
                        }
                        value = SerializationValue.Array(items);
                        break;
                    default: reader.Skip(wireType); break;
                }
            }
            return value;
        }

        private static object ReadScalar(ref Reader reader, SerializableType type, int wireType, string path)
        {
            if (type.Kind == TypeKind.Double)
            {
                Expect(wireType, Fixed64, path);
                return BinaryPrimitives.ReadDoubleLittleEndian(reader.ReadFixed(8));
            }

            Expect(wireType, Varint, path);
            var raw = reader.ReadVarint();
            return type.Kind switch
            {
                TypeKind.Boolean => raw != 0,
                TypeKind.Int32 => (int)raw,
                TypeKind.Int64 => (long)raw,
                _ => ((EnumType)type).FromNumber((int)raw)
            };
        }

        // ------------------------------------------------------------------ wire

        private static bool IsPackable(SerializableType type) =>
            type.Kind is TypeKind.Boolean or TypeKind.Int32 or TypeKind.Int64 or TypeKind.Enum or TypeKind.Double;

        private static void Expect(int actual, int expected, string path)
        {
            if (actual != expected)
                throw new SerializationException(path, $"wire type {actual} does not match the field (expected {expected}).");
        }

        private static string DecodeUtf8(ReadOnlySpan<byte> bytes, string path)
        {
            try
            {
                return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                throw new SerializationException(path, "the string is not valid UTF-8.");
            }
        }

        private static void WriteTag(IBufferWriter<byte> output, int number, int wireType) => WriteVarint(output, ((ulong)(uint)number << 3) | (uint)wireType);

        private static void WriteBytes(IBufferWriter<byte> output, int number, ReadOnlySpan<byte> bytes)
        {
            WriteTag(output, number, LengthDelimited);
            WriteVarint(output, (ulong)bytes.Length);
            output.Write(bytes);
        }

        private static void WriteVarint(IBufferWriter<byte> output, ulong value)
        {
            var span = output.GetSpan(10);
            var length = 0;
            while (value >= 0x80)
            {
                span[length++] = (byte)(value | 0x80);
                value >>= 7;
            }
            span[length++] = (byte)value;
            output.Advance(length);
        }

        private ref struct Reader(ReadOnlySpan<byte> data, string path)
        {
            private readonly ReadOnlySpan<byte> data = data;
            private int position;

            public readonly bool End => position >= data.Length;

            public (int Number, int WireType) ReadTag()
            {
                var tag = ReadVarint();
                var number = tag >> 3;
                if (number is 0 or > 536_870_911)
                    throw new SerializationException(path, $"invalid field number {number}.");
                return ((int)number, (int)(tag & 7));
            }

            public ulong ReadVarint()
            {
                ulong value = 0;
                for (var shift = 0; shift < 64; shift += 7)
                {
                    if (position >= data.Length)
                        throw Truncated();
                    var current = data[position++];
                    value |= (ulong)(current & 0x7F) << shift;
                    if (current < 0x80)
                        return value;
                }
                throw new SerializationException(path, "a varint is longer than 10 bytes.");
            }

            public ReadOnlySpan<byte> ReadFixed(int length)
            {
                if (data.Length - position < length)
                    throw Truncated();
                var bytes = data.Slice(position, length);
                position += length;
                return bytes;
            }

            public ReadOnlySpan<byte> ReadLengthDelimited()
            {
                var length = ReadVarint();
                if (length > (ulong)(data.Length - position))
                    throw Truncated();
                return ReadFixed((int)length);
            }

            public void Skip(int wireType)
            {
                switch (wireType)
                {
                    case Varint: ReadVarint(); break;
                    case Fixed64: ReadFixed(8); break;
                    case LengthDelimited: ReadLengthDelimited(); break;
                    case Fixed32: ReadFixed(4); break;
                    default: throw new SerializationException(path, $"unsupported wire type {wireType} (groups are deprecated).");
                }
            }

            private readonly SerializationException Truncated() => new(path, "the message is truncated.");
        }

        internal static string Invariant(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    }
}

using System.Globalization;
using System.Text;

namespace Butterfly.Serialization
{
    public enum ValueKind : byte
    {
        Null,
        Boolean,
        /// <summary>A whole number that fits in 64 bits.</summary>
        Integer,
        /// <summary>A binary floating-point number.</summary>
        Number,
        /// <summary>An exact decimal number.</summary>
        Decimal,
        String,
        Bytes,
        Timestamp,
        Array,
        Object
    }

    /// <summary>
    /// A value whose shape is only known at run time: what dynamic members (object, JsonNode...) become while they are
    /// serialized, and what schema-less reads produce. Immutable; objects keep the order of their members.
    /// </summary>
    public sealed class SerializationValue : IEquatable<SerializationValue>
    {
        private readonly object? value;

        private SerializationValue(ValueKind kind, object? value)
        {
            Kind = kind;
            this.value = value;
        }

        public ValueKind Kind { get; }
        public bool IsNull => Kind == ValueKind.Null;

        public static SerializationValue Null { get; } = new(ValueKind.Null, null);
        public static SerializationValue True { get; } = new(ValueKind.Boolean, true);
        public static SerializationValue False { get; } = new(ValueKind.Boolean, false);

        public static SerializationValue From(bool value) => value ? True : False;
        public static SerializationValue From(long value) => new(ValueKind.Integer, value);
        public static SerializationValue From(double value) => new(ValueKind.Number, value);
        public static SerializationValue From(decimal value) => new(ValueKind.Decimal, value);
        public static SerializationValue From(string? value) => value is null ? Null : new(ValueKind.String, value);
        public static SerializationValue From(byte[]? value) => value is null ? Null : new(ValueKind.Bytes, value);
        public static SerializationValue From(DateTimeOffset value) => new(ValueKind.Timestamp, value);
        public static SerializationValue Array(IEnumerable<SerializationValue> items) => new(ValueKind.Array, items.ToArray());
        public static SerializationValue Object(IEnumerable<KeyValuePair<string, SerializationValue>> members) => new(ValueKind.Object, members.ToArray());

        public bool AsBoolean() => Kind == ValueKind.Boolean ? (bool)value! : throw WrongKind("a boolean");
        public long AsInt64() => Kind switch
        {
            ValueKind.Integer => (long)value!,
            ValueKind.Decimal when (decimal)value! == decimal.Truncate((decimal)value!) && (decimal)value! is >= long.MinValue and <= long.MaxValue => (long)(decimal)value!,
            ValueKind.Number when double.IsInteger((double)value!) && Math.Abs((double)value!) < 9.2e18 => (long)(double)value!,
            _ => throw WrongKind("an integer")
        };
        public double AsDouble() => Kind switch
        {
            ValueKind.Integer => (long)value!,
            ValueKind.Number => (double)value!,
            ValueKind.Decimal => (double)(decimal)value!,
            _ => throw WrongKind("a number")
        };
        public decimal AsDecimal() => Kind switch
        {
            ValueKind.Integer => (long)value!,
            ValueKind.Decimal => (decimal)value!,
            ValueKind.Number => (decimal)(double)value!,
            _ => throw WrongKind("a number")
        };
        public string AsString() => Kind == ValueKind.String ? (string)value! : throw WrongKind("a string");
        public byte[] AsBytes() => Kind == ValueKind.Bytes ? (byte[])value! : throw WrongKind("bytes");
        public DateTimeOffset AsTimestamp() => Kind == ValueKind.Timestamp ? (DateTimeOffset)value! : throw WrongKind("a timestamp");

        public bool IsNumeric => Kind is ValueKind.Integer or ValueKind.Number or ValueKind.Decimal;

        public IReadOnlyList<SerializationValue> Items => Kind == ValueKind.Array ? (SerializationValue[])value! : [];
        public IReadOnlyList<KeyValuePair<string, SerializationValue>> Members => Kind == ValueKind.Object ? (KeyValuePair<string, SerializationValue>[])value! : [];

        /// <summary>A member of an object (null when missing or when this is not an object).</summary>
        public SerializationValue? this[string name] =>
            Members.FirstOrDefault(member => member.Key == name) is { Key: not null } found ? found.Value : null;

        public SerializationValue this[int index] => Items[index];

        public bool Equals(SerializationValue? other)
        {
            if (other is null)
                return false;
            if (ReferenceEquals(this, other))
                return true;
            if (IsNumeric && other.IsNumeric)
                return Kind == ValueKind.Number || other.Kind == ValueKind.Number ? AsDouble().Equals(other.AsDouble()) : AsDecimal() == other.AsDecimal();
            if (Kind != other.Kind)
                return false;

            return Kind switch
            {
                ValueKind.Null => true,
                ValueKind.Bytes => AsBytes().AsSpan().SequenceEqual(other.AsBytes()),
                ValueKind.Timestamp => AsTimestamp().UtcTicks == other.AsTimestamp().UtcTicks,
                ValueKind.Array => Items.SequenceEqual(other.Items),
                ValueKind.Object => Members.Count == other.Members.Count && Members.Zip(other.Members).All(pair => pair.First.Key == pair.Second.Key && pair.First.Value.Equals(pair.Second.Value)),
                _ => Equals(value, other.value)
            };
        }

        public override bool Equals(object? obj) => obj is SerializationValue other && Equals(other);

        public override int GetHashCode() => Kind switch
        {
            ValueKind.Null => 0,
            ValueKind.Integer or ValueKind.Number or ValueKind.Decimal => AsDouble().GetHashCode(),
            ValueKind.Array => Items.Count,
            ValueKind.Object => Members.Count,
            ValueKind.Bytes => AsBytes().Length,
            _ => value!.GetHashCode()
        };

        /// <summary>A compact JSON-like text, for diagnostics.</summary>
        public override string ToString()
        {
            var builder = new StringBuilder();
            Append(builder, this);
            return builder.ToString();

            static void Append(StringBuilder builder, SerializationValue value)
            {
                switch (value.Kind)
                {
                    case ValueKind.Null: builder.Append("null"); break;
                    case ValueKind.Boolean: builder.Append(value.AsBoolean() ? "true" : "false"); break;
                    case ValueKind.Integer: builder.Append(value.AsInt64().ToString(CultureInfo.InvariantCulture)); break;
                    case ValueKind.Number: builder.Append(value.AsDouble().ToString("R", CultureInfo.InvariantCulture)); break;
                    case ValueKind.Decimal: builder.Append(value.AsDecimal().ToString(CultureInfo.InvariantCulture)); break;
                    case ValueKind.String: builder.Append('"').Append(value.AsString().Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"'); break;
                    case ValueKind.Bytes: builder.Append('"').Append(Convert.ToBase64String(value.AsBytes())).Append('"'); break;
                    case ValueKind.Timestamp: builder.Append('"').Append(value.AsTimestamp().ToString("O", CultureInfo.InvariantCulture)).Append('"'); break;
                    case ValueKind.Array:
                        builder.Append('[');
                        for (var i = 0; i < value.Items.Count; i++)
                        {
                            if (i > 0) builder.Append(',');
                            Append(builder, value.Items[i]);
                        }
                        builder.Append(']');
                        break;
                    case ValueKind.Object:
                        builder.Append('{');
                        for (var i = 0; i < value.Members.Count; i++)
                        {
                            if (i > 0) builder.Append(',');
                            builder.Append('"').Append(value.Members[i].Key).Append("\":");
                            Append(builder, value.Members[i].Value);
                        }
                        builder.Append('}');
                        break;
                }
            }
        }

        private InvalidOperationException WrongKind(string expected) => new($"The value is {Kind}, not {expected}.");
    }
}

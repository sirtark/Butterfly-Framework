using System.Collections;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Butterfly.Serialization
{
    /// <summary>
    /// A value whose shape is decided at run time. Supported CLR types: <see cref="object"/> (primitives, dictionaries,
    /// lists and described contracts), <see cref="SerializationValue"/>, <see cref="JsonNode"/> and its subclasses, and
    /// <see cref="JsonElement"/>.
    /// </summary>
    public sealed class DynamicType : SerializableType
    {
        private readonly Func<object, SerializationValue> toValue;
        private readonly Func<SerializationValue, object?> fromValue;

        private DynamicType(Type clrType, Func<object, SerializationValue> toValue, Func<SerializationValue, object?> fromValue)
            : base(TypeKind.Dynamic, "dynamic", clrType)
        {
            this.toValue = toValue;
            this.fromValue = fromValue;
        }

        public SerializationValue ToValue(object? value) => value is null ? SerializationValue.Null : toValue(value);
        public object? FromValue(SerializationValue value) => fromValue(value);

        public override object? CreateDefault() => ClrType == typeof(JsonElement) ? fromValue(SerializationValue.Null) : null;

        /// <summary>Plain CLR values: reads produce bool, long, double, decimal, string, byte[], DateTimeOffset, List&lt;object?&gt; and Dictionary&lt;string, object?&gt;.</summary>
        public static DynamicType Object { get; } = new(typeof(object), FromObject, ToObject);
        public static DynamicType Value { get; } = new(typeof(SerializationValue), value => (SerializationValue)value, value => value);
        public static DynamicType JsonNode { get; } = new(typeof(JsonNode), value => FromNode((JsonNode)value), ToNode);
        public static DynamicType JsonObject { get; } = new(typeof(JsonObject), value => FromNode((JsonNode)value), value => ToNode(value) as JsonObject);
        public static DynamicType JsonArray { get; } = new(typeof(JsonArray), value => FromNode((JsonNode)value), value => ToNode(value) as JsonArray);
        public static DynamicType JsonValue { get; } = new(typeof(JsonValue), value => FromNode((JsonNode)value), value => ToNode(value) as JsonValue);
        public static DynamicType JsonElement { get; } = new(typeof(JsonElement), value => FromElement((JsonElement)value), ToElement);

        /// <summary>The dynamic descriptor of a CLR type, or null when it is not a dynamic type.</summary>
        public static DynamicType? For(Type type) => type switch
        {
            _ when type == typeof(object) => Object,
            _ when type == typeof(SerializationValue) => Value,
            _ when type == typeof(JsonNode) => JsonNode,
            _ when type == typeof(JsonObject) => JsonObject,
            _ when type == typeof(JsonArray) => JsonArray,
            _ when type == typeof(JsonValue) => JsonValue,
            _ when type == typeof(JsonElement) => JsonElement,
            _ => null
        };

        // ------------------------------------------------------------------ object

        private static SerializationValue FromObject(object value) => value switch
        {
            SerializationValue serialized => serialized,
            bool boolean => SerializationValue.From(boolean),
            byte or sbyte or short or ushort or int or uint or long => SerializationValue.From(Convert.ToInt64(value, CultureInfo.InvariantCulture)),
            ulong unsigned => unsigned <= long.MaxValue ? SerializationValue.From((long)unsigned) : SerializationValue.From((decimal)unsigned),
            float single => SerializationValue.From((double)single),
            double number => SerializationValue.From(number),
            decimal amount => SerializationValue.From(amount),
            string text => SerializationValue.From(text),
            char character => SerializationValue.From(character.ToString()),
            byte[] bytes => SerializationValue.From(bytes),
            DateTimeOffset timestamp => SerializationValue.From(timestamp),
            DateTime dateTime => SerializationValue.From(ScalarText.ToTimestamp(dateTime)),
            Guid guid => SerializationValue.From(guid.ToString("D")),
            Enum enumeration => SerializationValue.From(enumeration.ToString()),
            System.Text.Json.Nodes.JsonNode node => FromNode(node),
            System.Text.Json.JsonElement element => FromElement(element),
            IDictionary<string, object?> dictionary => SerializationValue.Object(dictionary.Select(pair => new KeyValuePair<string, SerializationValue>(pair.Key, ToValueOrNull(pair.Value)))),
            IEnumerable<KeyValuePair<string, object?>> pairs => SerializationValue.Object(pairs.Select(pair => new KeyValuePair<string, SerializationValue>(pair.Key, ToValueOrNull(pair.Value)))),
            IDictionary legacy => SerializationValue.Object(legacy.Cast<DictionaryEntry>().Select(entry => new KeyValuePair<string, SerializationValue>(Convert.ToString(entry.Key, CultureInfo.InvariantCulture)!, ToValueOrNull(entry.Value)))),
            IEnumerable items => SerializationValue.Array(items.Cast<object?>().Select(ToValueOrNull)),
            _ => FromContract(value)
        };

        private static SerializationValue ToValueOrNull(object? value) => value is null ? SerializationValue.Null : FromObject(value);

        // A described contract (generated descriptor) inside a dynamic value keeps its declared member names.
        private static SerializationValue FromContract(object value)
        {
            if (SerializationRegistry.TryGet(value.GetType(), out var type))
                return ValueConverter.ToValue(type, value, ValueConventions.Dynamic);
            throw new SerializationException("", $"{value.GetType()} cannot be serialized dynamically: it is not a described contract.");
        }

        private static object? ToObject(SerializationValue value) => value.Kind switch
        {
            ValueKind.Null => null,
            ValueKind.Boolean => value.AsBoolean(),
            ValueKind.Integer => value.AsInt64(),
            ValueKind.Number => value.AsDouble(),
            ValueKind.Decimal => value.AsDecimal(),
            ValueKind.String => value.AsString(),
            ValueKind.Bytes => value.AsBytes(),
            ValueKind.Timestamp => value.AsTimestamp(),
            ValueKind.Array => value.Items.Select(ToObject).ToList(),
            _ => value.Members.ToDictionary(member => member.Key, member => ToObject(member.Value))
        };

        // ------------------------------------------------------------------ System.Text.Json

        private static SerializationValue FromNode(System.Text.Json.Nodes.JsonNode? node)
        {
            switch (node)
            {
                case null:
                    return SerializationValue.Null;
                case System.Text.Json.Nodes.JsonObject json:
                    return SerializationValue.Object(json.Select(pair => new KeyValuePair<string, SerializationValue>(pair.Key, FromNode(pair.Value))));
                case System.Text.Json.Nodes.JsonArray array:
                    return SerializationValue.Array(array.Select(FromNode));
            }

            var jsonValue = (System.Text.Json.Nodes.JsonValue)node;
            if (jsonValue.TryGetValue<JsonElement>(out var element))
                return FromElement(element);
            if (jsonValue.TryGetValue<bool>(out var boolean)) return SerializationValue.From(boolean);
            if (jsonValue.TryGetValue<long>(out var integer)) return SerializationValue.From(integer);
            if (jsonValue.TryGetValue<int>(out var int32)) return SerializationValue.From(int32);
            if (jsonValue.TryGetValue<decimal>(out var amount)) return SerializationValue.From(amount);
            if (jsonValue.TryGetValue<double>(out var number)) return SerializationValue.From(number);
            if (jsonValue.TryGetValue<string>(out var text)) return SerializationValue.From(text);
            if (jsonValue.TryGetValue<DateTimeOffset>(out var timestamp)) return SerializationValue.From(timestamp);
            if (jsonValue.TryGetValue<DateTime>(out var dateTime)) return SerializationValue.From(ScalarText.ToTimestamp(dateTime));
            if (jsonValue.TryGetValue<Guid>(out var guid)) return SerializationValue.From(guid.ToString("D"));
            return SerializationValue.From(jsonValue.ToJsonString());
        }

        private static System.Text.Json.Nodes.JsonNode? ToNode(SerializationValue value) => value.Kind switch
        {
            ValueKind.Null => null,
            ValueKind.Boolean => System.Text.Json.Nodes.JsonValue.Create(value.AsBoolean()),
            ValueKind.Integer => System.Text.Json.Nodes.JsonValue.Create(value.AsInt64()),
            ValueKind.Number => double.IsFinite(value.AsDouble()) ? System.Text.Json.Nodes.JsonValue.Create(value.AsDouble()) : System.Text.Json.Nodes.JsonValue.Create(value.AsDouble().ToString(CultureInfo.InvariantCulture)),
            ValueKind.Decimal => System.Text.Json.Nodes.JsonValue.Create(value.AsDecimal()),
            ValueKind.String => System.Text.Json.Nodes.JsonValue.Create(value.AsString()),
            ValueKind.Bytes => System.Text.Json.Nodes.JsonValue.Create(Convert.ToBase64String(value.AsBytes())),
            ValueKind.Timestamp => System.Text.Json.Nodes.JsonValue.Create(ScalarText.FormatTimestamp(value.AsTimestamp())),
            ValueKind.Array => new System.Text.Json.Nodes.JsonArray([.. value.Items.Select(ToNode)]),
            _ => new System.Text.Json.Nodes.JsonObject(value.Members.Select(member => new KeyValuePair<string, System.Text.Json.Nodes.JsonNode?>(member.Key, ToNode(member.Value))))
        };

        private static SerializationValue FromElement(JsonElement element) => element.ValueKind switch
        {
            JsonValueKind.Object => SerializationValue.Object(element.EnumerateObject().Select(property => new KeyValuePair<string, SerializationValue>(property.Name, FromElement(property.Value)))),
            JsonValueKind.Array => SerializationValue.Array(element.EnumerateArray().Select(FromElement)),
            JsonValueKind.String => SerializationValue.From(element.GetString()),
            JsonValueKind.Number => element.TryGetInt64(out var integer) ? SerializationValue.From(integer)
                : element.TryGetDecimal(out var amount) && !element.GetRawText().Contains('e', StringComparison.OrdinalIgnoreCase) ? SerializationValue.From(amount)
                : SerializationValue.From(element.GetDouble()),
            JsonValueKind.True => SerializationValue.True,
            JsonValueKind.False => SerializationValue.False,
            _ => SerializationValue.Null
        };

        private static object ToElement(SerializationValue value)
        {
            // Written as JSON and parsed back: a JsonElement can only come from a document.
            var node = ToNode(value);
            using var document = JsonDocument.Parse(node?.ToJsonString() ?? "null");
            return document.RootElement.Clone();
        }
    }
}

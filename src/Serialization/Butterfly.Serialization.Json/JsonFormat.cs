using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Butterfly.Serialization.Json
{
    /// <summary>
    /// JSON: camelCase members and enum names by default, bytes as Base64, timestamps as ISO 8601, NaN and infinities as
    /// strings. Only what JSON requires is escaped ("+", "&lt;" and accents stay readable): payloads are not embedded in HTML.
    /// </summary>
    public sealed class JsonFormat : ValueFormat
    {
        public static JsonFormat Instance { get; } = new();

        public override string Name => "json";
        public override string MediaType => "application/json";
        public override bool IsText => true;
        public override ValueConventions Conventions { get; } = new(NamingPolicy.CamelCase, EnumsAsNames: true, ScalarsFromText: false);

        public static JsonWriterOptions WriterOptions(SerializationProfile profile) => new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Indented = profile.Indented,
            MaxDepth = profile.MaxDepth + 8
        };

        public override void WriteValue(IBufferWriter<byte> output, SerializationValue value, SerializationProfile profile)
        {
            using var writer = new Utf8JsonWriter(output, WriterOptions(profile));
            Write(writer, value);
        }

        public override SerializationValue ReadValue(ReadOnlyMemory<byte> input, SerializationProfile profile)
        {
            try
            {
                using var document = JsonDocument.Parse(input, new JsonDocumentOptions { MaxDepth = profile.MaxDepth, CommentHandling = JsonCommentHandling.Skip });
                return FromElement(document.RootElement);
            }
            catch (JsonException exception)
            {
                throw new SerializationException("", $"invalid JSON: {exception.Message}", exception);
            }
        }

        /// <summary>Writes a value into an existing writer (to compose envelopes such as JSON-RPC responses).</summary>
        public static void Write(Utf8JsonWriter writer, SerializationValue value)
        {
            switch (value.Kind)
            {
                case ValueKind.Null: writer.WriteNullValue(); break;
                case ValueKind.Boolean: writer.WriteBooleanValue(value.AsBoolean()); break;
                case ValueKind.Integer: writer.WriteNumberValue(value.AsInt64()); break;
                case ValueKind.Number:
                    var number = value.AsDouble();
                    if (double.IsFinite(number))
                        writer.WriteNumberValue(number);
                    else
                        writer.WriteStringValue(double.IsNaN(number) ? "NaN" : number > 0 ? "Infinity" : "-Infinity");
                    break;
                case ValueKind.Decimal: writer.WriteNumberValue(value.AsDecimal()); break;
                case ValueKind.String: writer.WriteStringValue(value.AsString()); break;
                case ValueKind.Bytes: writer.WriteBase64StringValue(value.AsBytes()); break;
                case ValueKind.Timestamp: writer.WriteStringValue(ScalarText.FormatTimestamp(value.AsTimestamp())); break;
                case ValueKind.Array:
                    writer.WriteStartArray();
                    foreach (var item in value.Items)
                        Write(writer, item);
                    writer.WriteEndArray();
                    break;
                case ValueKind.Object:
                    writer.WriteStartObject();
                    foreach (var (name, member) in value.Members)
                    {
                        writer.WritePropertyName(name);
                        Write(writer, member);
                    }
                    writer.WriteEndObject();
                    break;
            }
        }

        /// <summary>The value of an already parsed element (for example the params of a JSON-RPC request).</summary>
        public static SerializationValue FromElement(JsonElement element) => element.ValueKind switch
        {
            JsonValueKind.Object => SerializationValue.Object(element.EnumerateObject().Select(property => new KeyValuePair<string, SerializationValue>(property.Name, FromElement(property.Value)))),
            JsonValueKind.Array => SerializationValue.Array(element.EnumerateArray().Select(FromElement)),
            JsonValueKind.String => SerializationValue.From(element.GetString()),
            JsonValueKind.Number => Number(element),
            JsonValueKind.True => SerializationValue.True,
            JsonValueKind.False => SerializationValue.False,
            _ => SerializationValue.Null
        };

        // Integers stay exact; other numbers are decimal when they fit (no exponent), double otherwise.
        private static SerializationValue Number(JsonElement element)
        {
            if (element.TryGetInt64(out var integer))
                return SerializationValue.From(integer);
            var raw = element.GetRawText();
            if (raw.IndexOfAny(['e', 'E']) < 0 && element.TryGetDecimal(out var amount))
                return SerializationValue.From(amount);
            return SerializationValue.From(element.GetDouble());
        }

        /// <summary>Reads a typed value from an already parsed element.</summary>
        public object? Read(JsonElement element, SerializableType type, SerializationProfile? profile = null) =>
            ValueConverter.FromValue(type, FromElement(element), Conventions, profile);

        /// <summary>Writes a typed value into an existing writer.</summary>
        public void Write(Utf8JsonWriter writer, SerializableType type, object? value, SerializationProfile? profile = null) =>
            Write(writer, ValueConverter.ToValue(type, value, Conventions, profile));
    }
}

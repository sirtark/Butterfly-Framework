using System.Collections;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Butterfly.Workflows.Serialization
{
    // The JSON shape shared by definitions, instances, component settings and stores.
    public static class WorkflowJson
    {
        public static JsonSerializerOptions Options { get; } = CreateOptions(indented: false);
        public static JsonSerializerOptions IndentedOptions { get; } = CreateOptions(indented: true);

        public static string Serialize<T>(T value, bool indented = false)
            => JsonSerializer.Serialize(value, indented ? IndentedOptions : Options);

        public static T Deserialize<T>(string json)
            => JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException($"The JSON does not contain a {typeof(T).Name}.");

        public static JsonObject? ToObject(object? value)
            => value switch
            {
                null => null,
                JsonObject json => json,
                _ => JsonSerializer.SerializeToNode(value, Options) as JsonObject
                    ?? throw new ArgumentException($"{value.GetType()} does not serialize to a JSON object.", nameof(value))
            };

        public static JsonNode? ToNode(object? value)
            => value switch
            {
                null => null,
                JsonNode json => json,
                _ => JsonSerializer.SerializeToNode(value, Options)
            };

        static JsonSerializerOptions CreateOptions(bool indented)
        {
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                WriteIndented = indented,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { SkipEmptyCollections } }
            };
            options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
            options.MakeReadOnly();
            return options;
        }

        static void SkipEmptyCollections(JsonTypeInfo typeInfo)
        {
            foreach (var property in typeInfo.Properties)
            {
                if (typeof(ICollection).IsAssignableFrom(property.PropertyType))
                    property.ShouldSerialize = (_, value) => value is ICollection { Count: > 0 };
            }
        }
    }
}

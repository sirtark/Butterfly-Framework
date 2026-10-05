using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Butterfly.Workflows
{
    internal static class WorkflowValuePath
    {
        public static JsonNode? Resolve(WorkflowContext context, string path)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path, nameof(path));

            var segments = path.Split('.');
            var (root, rootIndexes) = ParseSegment(segments[0]);
            var current = root switch
            {
                "variables" => context.Variables,
                "input" => context.Instance.Input,
                "event" => context.Event?.Payload,
                "outputs" => context.Instance.Outputs,
                "settings" => context.Settings,
                _ => throw new WorkflowException($"The path '{path}' starts with '{root}'; use variables, input, event, outputs or settings.")
            };
            current = ApplyIndexes(current, rootIndexes);

            foreach (var segment in segments.Skip(1))
            {
                if (current is null)
                    return null;
                var (name, indexes) = ParseSegment(segment);
                current = current is JsonObject json && json.TryGetPropertyValue(name, out var child) ? child : null;
                current = ApplyIndexes(current, indexes);
            }
            return current;
        }

        static (string Name, List<int> Indexes) ParseSegment(string segment)
        {
            var bracket = segment.IndexOf('[');
            if (bracket < 0)
                return (segment, []);

            var indexes = new List<int>();
            foreach (var part in segment[bracket..].Split('[', StringSplitOptions.RemoveEmptyEntries))
                indexes.Add(int.Parse(part.TrimEnd(']'), CultureInfo.InvariantCulture));
            return (segment[..bracket], indexes);
        }

        static JsonNode? ApplyIndexes(JsonNode? node, List<int> indexes)
        {
            foreach (var index in indexes)
                node = node is JsonArray array && index >= 0 && index < array.Count ? array[index] : null;
            return node;
        }
    }

    internal static partial class WorkflowTemplate
    {
        [GeneratedRegex(@"\{([A-Za-z_][A-Za-z0-9_.\[\]-]*)\}")]
        private static partial Regex Placeholder();

        public static string Render(WorkflowContext context, string template)
            => Placeholder().Replace(template, match => JsonValues.ToText(context.GetValue(match.Groups[1].Value)));
    }

    internal static class JsonValues
    {
        public static bool AreEqual(JsonNode? left, JsonNode? right)
            => TryGetNumber(left, out var a) && TryGetNumber(right, out var b) ? a == b : JsonNode.DeepEquals(left, right);

        public static int? Compare(JsonNode? left, JsonNode? right)
        {
            if (TryGetNumber(left, out var a) && TryGetNumber(right, out var b))
                return a.CompareTo(b);
            if (TryGetString(left, out var s) && TryGetString(right, out var t))
                return string.CompareOrdinal(s, t);
            return null;
        }

        public static bool Contains(JsonNode? container, JsonNode? item)
            => container switch
            {
                JsonArray array => array.Any(element => AreEqual(element, item)),
                JsonObject json => TryGetString(item, out var key) && json.ContainsKey(key),
                _ => TryGetString(container, out var text) && TryGetString(item, out var part) && text.Contains(part, StringComparison.Ordinal)
            };

        public static bool IsEmpty(JsonNode? node)
            => node switch
            {
                null => true,
                JsonArray array => array.Count == 0,
                JsonObject json => json.Count == 0,
                _ => TryGetString(node, out var text) && text.Length == 0
            };

        public static string ToText(JsonNode? node)
            => node is null ? string.Empty : TryGetString(node, out var text) ? text : node.ToJsonString();

        public static bool TryGetNumber(JsonNode? node, out double number)
        {
            number = 0;
            return node is JsonValue value
                && value.GetValueKind() == JsonValueKind.Number
                && double.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number);
        }

        public static bool TryGetString(JsonNode? node, out string text)
        {
            if (node is JsonValue value && value.GetValueKind() == JsonValueKind.String)
            {
                text = value.GetValue<string>();
                return true;
            }
            text = string.Empty;
            return false;
        }

        public static JsonNode? Clone(JsonNode? node)
            => node?.DeepClone();
    }
}

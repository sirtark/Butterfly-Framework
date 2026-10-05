using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace Butterfly.Scripting.Lua
{
    public static class LuaValues
    {
        public const int MaxDepth = 64;

        public static object FromNumber(double value)
            => double.IsFinite(value) && value == Math.Floor(value) && value >= long.MinValue && value < long.MaxValue
                ? (object)(long)value
                : value;

        public static object FromTable(IEnumerable<KeyValuePair<object, object?>> entries)
        {
            ArgumentNullException.ThrowIfNull(entries, nameof(entries));

            var list = entries.ToList();
            if (list.Count > 0 && list.All(entry => entry.Key is long index && index >= 1 && index <= list.Count))
            {
                var items = new object?[list.Count];
                foreach (var (key, value) in list)
                    items[(long)key - 1] = value;
                return items.ToList();
            }

            var dictionary = new Dictionary<string, object?>(list.Count);
            foreach (var (key, value) in list)
                dictionary[KeyToString(key)] = value;
            return dictionary;
        }

        internal static object? FromClr(object? value, int depth = 0)
        {
            if (depth > MaxDepth)
                throw new NotSupportedException($"The value is nested more than {MaxDepth} levels deep or contains a cycle.");

            return value switch
            {
                null => null,
                bool or long or double or string => value,
                char character => character.ToString(),
                sbyte or byte or short or ushort or int or uint => Convert.ToInt64(value, CultureInfo.InvariantCulture),
                ulong number => number <= long.MaxValue ? (object)(long)number : (double)number,
                float or decimal => Convert.ToDouble(value, CultureInfo.InvariantCulture),
                Enum => value.ToString(),
                DateTime date => date.ToString("O", CultureInfo.InvariantCulture),
                DateTimeOffset date => date.ToString("O", CultureInfo.InvariantCulture),
                DateOnly date => date.ToString("O", CultureInfo.InvariantCulture),
                TimeOnly time => time.ToString("O", CultureInfo.InvariantCulture),
                JsonElement json => FromJson(json, depth),
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                Delegate or MemberInfo or Assembly => throw new NotSupportedException($"Values of type {value.GetType()} cannot be passed to Lua."),
                IDictionary or IEnumerable<KeyValuePair<string, object?>> => FromDictionary(ScriptParameters.From(value), depth),
                IEnumerable sequence => sequence.Cast<object?>().Select(item => FromClr(item, depth + 1)).ToList(),
                _ => FromDictionary(ScriptParameters.From(value), depth)
            };
        }

        static Dictionary<string, object?> FromDictionary(IReadOnlyDictionary<string, object> dictionary, int depth)
            => dictionary.ToDictionary(entry => entry.Key, entry => FromClr(entry.Value, depth + 1));

        static object? FromJson(JsonElement element, int depth)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    var dictionary = new Dictionary<string, object?>();
                    foreach (var property in element.EnumerateObject())
                        dictionary[property.Name] = FromClr(property.Value, depth + 1);
                    return dictionary;
                case JsonValueKind.Array:
                    return element.EnumerateArray().Select(item => FromClr(item, depth + 1)).ToList();
                case JsonValueKind.String:
                    return element.GetString();
                case JsonValueKind.Number:
                    return element.TryGetInt64(out var integer) ? (object)integer : element.GetDouble();
                case JsonValueKind.True:
                    return true;
                case JsonValueKind.False:
                    return false;
                default:
                    return null;
            }
        }

        static string KeyToString(object key)
            => key switch
            {
                string text => text,
                long integer => integer.ToString(CultureInfo.InvariantCulture),
                double number => number.ToString("R", CultureInfo.InvariantCulture),
                bool boolean => boolean ? "true" : "false",
                _ => throw new ScriptException(ScriptingLanguage.Lua, "The Lua script returned a table whose keys are tables, which cannot be converted to .NET.")
            };
    }
}

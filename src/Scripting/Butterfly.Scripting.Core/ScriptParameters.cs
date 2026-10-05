using System.Collections;
using System.Globalization;
using System.Reflection;

namespace Butterfly.Scripting
{
    public static class ScriptParameters
    {
        public static IReadOnlyDictionary<string, object> Empty { get; } = new Dictionary<string, object>();

        public static IReadOnlyDictionary<string, object> From(object? parameters)
            => parameters switch
            {
                null => Empty,
                IReadOnlyDictionary<string, object> dictionary => dictionary,
                IEnumerable<KeyValuePair<string, object?>> pairs => pairs.ToDictionary(pair => pair.Key, pair => pair.Value!),
                IDictionary dictionary => dictionary.Keys.Cast<object>().ToDictionary(key => Convert.ToString(key, CultureInfo.InvariantCulture)!, key => dictionary[key]!),
                _ => parameters.GetType()
                    .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(property => property.CanRead && property.GetIndexParameters().Length == 0)
                    .ToDictionary(property => property.Name, property => property.GetValue(parameters)!)
            };
    }
}

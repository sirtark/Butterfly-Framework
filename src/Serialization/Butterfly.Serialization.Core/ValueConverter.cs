using System.Globalization;

namespace Butterfly.Serialization
{
    /// <summary>What a format expects from the typed conversion.</summary>
    /// <param name="DefaultNaming">Naming used when the profile says <see cref="NamingPolicy.Default"/>.</param>
    /// <param name="EnumsAsNames">Whether enums are names (text formats) or numbers (binary formats) by default.</param>
    /// <param name="ScalarsFromText">Accept text for any scalar ("42" for an int, "true" for a bool): formats whose scalars are untyped text (YAML, CSV, XML).</param>
    public sealed record ValueConventions(NamingPolicy DefaultNaming, bool EnumsAsNames, bool ScalarsFromText)
    {
        /// <summary>Contracts nested in dynamic values: declared names, enums as names.</summary>
        public static ValueConventions Dynamic { get; } = new(NamingPolicy.AsDeclared, true, false);
    }

    /// <summary>
    /// Converts between typed values and <see cref="SerializationValue"/> trees, applying the profile: visible members,
    /// naming, enum format, null and default omission, and depth limits. Formats that only know their own syntax (JSON,
    /// YAML, MessagePack, CBOR, CSV) rely on it, so they all share the same semantics.
    /// </summary>
    public static class ValueConverter
    {
        public static SerializationValue ToValue(SerializableType type, object? value, ValueConventions conventions, SerializationProfile? profile = null) =>
            Write(type, value, profile ?? SerializationProfile.Default, conventions, "", 0);

        /// <exception cref="SerializationException">The value does not match the type.</exception>
        public static object? FromValue(SerializableType type, SerializationValue value, ValueConventions conventions, SerializationProfile? profile = null) =>
            Read(type, value, profile ?? SerializationProfile.Default, conventions, "", 0) ?? type.CreateDefault();

        private static SerializationValue Write(SerializableType type, object? value, SerializationProfile profile, ValueConventions conventions, string path, int depth)
        {
            if (value is null)
                return SerializationValue.Null;
            if (depth > profile.MaxDepth)
                throw new SerializationException(path, $"the value is nested deeper than {profile.MaxDepth} levels.");

            switch (type.Kind)
            {
                case TypeKind.Boolean: return SerializationValue.From((bool)value);
                case TypeKind.Int32: return SerializationValue.From((long)(int)value);
                case TypeKind.Int64: return SerializationValue.From((long)value);
                case TypeKind.Double: return SerializationValue.From((double)value);
                case TypeKind.Decimal: return SerializationValue.From((decimal)value);
                case TypeKind.String: return SerializationValue.From((string)value);
                case TypeKind.Bytes: return SerializationValue.From((byte[])value);
                case TypeKind.Timestamp: return SerializationValue.From(ScalarText.ToTimestamp(value));
                case TypeKind.Guid: return SerializationValue.From(((Guid)value).ToString("D"));
                case TypeKind.Duration: return SerializationValue.From(ScalarText.Format(type, value));
                case TypeKind.Enum:
                    var enumeration = (EnumType)type;
                    return profile.WritesEnumsAsNames(conventions.EnumsAsNames)
                        ? SerializationValue.From(enumeration.GetName(value))
                        : SerializationValue.From(enumeration.ToNumber(value));
                case TypeKind.Dynamic:
                    return ((DynamicType)type).ToValue(value);
                case TypeKind.List:
                    var list = (ListType)type;
                    return SerializationValue.Array(list.Enumerate(value).Select((item, index) => Write(list.Element, item, profile, conventions, SerializationException.Item(path, index), depth + 1)));
                case TypeKind.Map:
                    var map = (MapType)type;
                    return SerializationValue.Object(map.Enumerate(value)
                        .Where(entry => entry.Value is not null || !profile.IgnoreNullValues)
                        .Select(entry => new KeyValuePair<string, SerializationValue>(entry.Key, Write(map.Value, entry.Value, profile, conventions, SerializationException.Child(path, entry.Key), depth + 1))));
                default:
                    var members = new List<KeyValuePair<string, SerializationValue>>();
                    foreach (var member in ((ObjectType)type).Members)
                    {
                        if (!member.IsVisibleIn(profile))
                            continue;
                        var memberValue = member.Get(value);
                        if (memberValue is null && profile.IgnoreNullValues)
                            continue;
                        if (profile.IgnoreDefaultValues && ScalarText.IsDefault(member.Type, memberValue))
                            continue;
                        var name = profile.MemberName(member, conventions.DefaultNaming);
                        members.Add(new(name, Write(member.Type, memberValue, profile, conventions, SerializationException.Child(path, name), depth + 1)));
                    }
                    return SerializationValue.Object(members);
            }
        }

        private static object? Read(SerializableType type, SerializationValue value, SerializationProfile profile, ValueConventions conventions, string path, int depth)
        {
            if (value.IsNull)
                return null;
            if (depth > profile.MaxDepth)
                throw new SerializationException(path, $"nested deeper than {profile.MaxDepth} levels.");

            var text = value.Kind == ValueKind.String ? value.AsString() : null;
            switch (type.Kind)
            {
                case TypeKind.Boolean:
                    if (value.Kind == ValueKind.Boolean)
                        return value.AsBoolean();
                    return FromText(type, text, conventions.ScalarsFromText, path, "expected true or false.");

                case TypeKind.Int32:
                    if (value.IsNumeric && TryInteger(value, out var int32) && int32 is >= int.MinValue and <= int.MaxValue)
                        return (int)int32;
                    return FromText(type, text, conventions.ScalarsFromText, path, "expected a 32-bit integer.");

                case TypeKind.Int64:
                    // Large integers lose precision in JavaScript, so they may also arrive as strings.
                    if (value.IsNumeric && TryInteger(value, out var int64))
                        return int64;
                    return FromText(type, text, allowed: true, path, "expected a 64-bit integer.");

                case TypeKind.Double:
                    if (value.IsNumeric)
                        return value.AsDouble();
                    return FromText(type, text, conventions.ScalarsFromText || text is "NaN" or "Infinity" or "-Infinity", path, "expected a number.");

                case TypeKind.Decimal:
                    if (value.IsNumeric)
                    {
                        try
                        {
                            return value.AsDecimal();
                        }
                        catch (OverflowException)
                        {
                            throw new SerializationException(path, "the number is out of the decimal range.");
                        }
                    }
                    return FromText(type, text, allowed: true, path, "expected a decimal number.");

                case TypeKind.String:
                    if (text is not null)
                        return text;
                    if (conventions.ScalarsFromText && value.Kind is ValueKind.Integer or ValueKind.Number or ValueKind.Decimal or ValueKind.Boolean or ValueKind.Timestamp)
                        return value.Kind == ValueKind.Timestamp ? ScalarText.FormatTimestamp(value.AsTimestamp()) : value.ToString();
                    throw new SerializationException(path, "expected a string.");

                case TypeKind.Bytes:
                    if (value.Kind == ValueKind.Bytes)
                        return value.AsBytes();
                    return FromText(type, text, allowed: true, path, "expected Base64 text or bytes.");

                case TypeKind.Timestamp:
                    if (value.Kind == ValueKind.Timestamp)
                        return ScalarText.FromTimestamp(type, value.AsTimestamp());
                    return FromText(type, text, allowed: true, path, "expected an ISO 8601 date and time.");

                case TypeKind.Guid:
                    if (value.Kind == ValueKind.Bytes && value.AsBytes().Length == 16)
                        return new Guid(value.AsBytes());
                    return FromText(type, text, allowed: true, path, "expected a GUID.");

                case TypeKind.Duration:
                    return FromText(type, text, allowed: true, path, "expected a duration (d.hh:mm:ss).");

                case TypeKind.Enum:
                    var enumeration = (EnumType)type;
                    if (value.IsNumeric && TryInteger(value, out var number))
                        return enumeration.FromNumber(number);
                    if (text is not null && enumeration.TryParse(text, out var member))
                        return member;
                    throw new SerializationException(path, $"expected one of {string.Join(", ", enumeration.Members.Select(m => m.Name))}.");

                case TypeKind.Dynamic:
                    return ((DynamicType)type).FromValue(value);

                case TypeKind.List:
                    if (value.Kind != ValueKind.Array)
                        throw new SerializationException(path, "expected a list.");
                    var list = (ListType)type;
                    var items = new List<object?>(value.Items.Count);
                    for (var i = 0; i < value.Items.Count; i++)
                        items.Add(Read(list.Element, value.Items[i], profile, conventions, SerializationException.Item(path, i), depth + 1) ?? list.Element.CreateDefault());
                    return list.Create(items);

                case TypeKind.Map:
                    if (value.Kind != ValueKind.Object)
                        throw new SerializationException(path, "expected a map.");
                    var map = (MapType)type;
                    return map.Create([.. value.Members.Select(entry => new KeyValuePair<string, object?>(entry.Key,
                        Read(map.Value, entry.Value, profile, conventions, SerializationException.Child(path, entry.Key), depth + 1) ?? (map.ValueIsOptional ? null : map.Value.CreateDefault())))]);

                default:
                    if (value.Kind != ValueKind.Object)
                        throw new SerializationException(path, "expected an object.");
                    var objectType = (ObjectType)type;
                    var values = objectType.CreateDefaultValues();
                    foreach (var (name, memberValue) in value.Members)
                    {
                        var target = objectType.FindMember(name, profile, conventions.DefaultNaming);
                        if (target is null)
                        {
                            if (profile.RejectUnknownMembers)
                                throw new SerializationException(SerializationException.Child(path, name), "unknown member.");
                            continue;
                        }
                        // Members the profile hides cannot be set from the input.
                        if (!target.IsVisibleIn(profile))
                            continue;
                        values[target.Index] = Read(target.Type, memberValue, profile, conventions, SerializationException.Child(path, name), depth + 1) ?? target.CreateDefault();
                    }
                    return objectType.Create(values);
            }
        }

        private static object? FromText(SerializableType type, string? text, bool allowed, string path, string problem)
        {
            if (text is not null && allowed && ScalarText.TryParse(type, type.Kind == TypeKind.String ? text : text.Trim(), out var parsed))
                return parsed;
            throw new SerializationException(path, text is null || !allowed ? problem : $"'{text}' is not a valid {type}.");
        }

        private static bool TryInteger(SerializationValue value, out long result)
        {
            try
            {
                result = value.AsInt64();
                return true;
            }
            catch (InvalidOperationException)
            {
                result = 0;
                return false;
            }
        }

        internal static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);
    }
}

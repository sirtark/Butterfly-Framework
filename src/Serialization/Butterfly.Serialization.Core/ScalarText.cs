using System.Globalization;

namespace Butterfly.Serialization
{
    /// <summary>Text forms of scalars shared by every format, so they all agree on how a value is written.</summary>
    public static class ScalarText
    {
        public static DateTimeOffset ToTimestamp(object value) => value switch
        {
            DateTimeOffset offset => offset,
            // Unspecified DateTime values are taken as UTC: a serializer cannot know the writer's local time zone.
            DateTime dateTime => dateTime.Kind == DateTimeKind.Local ? new DateTimeOffset(dateTime) : new DateTimeOffset(System.DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)),
            _ => throw new ArgumentException($"A timestamp cannot be a {value.GetType()}.", nameof(value))
        };

        // Both branches are boxed on purpose: as a plain conditional, DateTime would convert back to DateTimeOffset.
        /// <summary>The CLR value of a timestamp, as the declared type expects (DateTime values are UTC).</summary>
        public static object FromTimestamp(SerializableType type, DateTimeOffset value) =>
            type.ClrType == typeof(DateTime) ? (object)value.UtcDateTime : (object)value;

        /// <summary>ISO 8601 with offset and full precision ("2026-10-04T15:30:00.0000000+00:00").</summary>
        public static string FormatTimestamp(object value) => ToTimestamp(value).ToString("O", CultureInfo.InvariantCulture);

        public static bool TryParseTimestamp(SerializableType type, string text, out object? value)
        {
            if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            {
                value = FromTimestamp(type, parsed);
                return true;
            }
            value = null;
            return false;
        }

        /// <summary>
        /// Parses a scalar from text (query strings, XML content, CSV cells). Booleans accept true/false and 1/0; numbers
        /// use the invariant culture.
        /// </summary>
        public static bool TryParse(SerializableType type, string text, out object? value)
        {
            value = null;
            switch (type.Kind)
            {
                case TypeKind.String:
                    value = text;
                    return true;
                case TypeKind.Boolean:
                    if (text is "1" || text.Equals("true", StringComparison.OrdinalIgnoreCase)) value = true;
                    else if (text is "0" || text.Equals("false", StringComparison.OrdinalIgnoreCase)) value = false;
                    return value is not null;
                case TypeKind.Int32 when int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var int32):
                    value = int32;
                    return true;
                case TypeKind.Int64 when long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var int64):
                    value = int64;
                    return true;
                case TypeKind.Double:
                    switch (text)
                    {
                        case "NaN" or ".nan" or ".NaN": value = double.NaN; return true;
                        case "Infinity" or "INF" or ".inf" or ".Inf": value = double.PositiveInfinity; return true;
                        case "-Infinity" or "-INF" or "-.inf" or "-.Inf": value = double.NegativeInfinity; return true;
                    }
                    if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                    {
                        value = number;
                        return true;
                    }
                    return false;
                case TypeKind.Decimal when decimal.TryParse(text, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var amount):
                    value = amount;
                    return true;
                case TypeKind.Guid when System.Guid.TryParse(text, out var guid):
                    value = guid;
                    return true;
                case TypeKind.Timestamp:
                    return TryParseTimestamp(type, text, out value);
                case TypeKind.Duration:
                    // "1.02:03:04.5" (.NET) or ISO 8601 "P1DT2H3M4.5S" (XML Schema).
                    if (TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var duration))
                    {
                        value = duration;
                        return true;
                    }
                    try
                    {
                        value = System.Xml.XmlConvert.ToTimeSpan(text);
                        return true;
                    }
                    catch (FormatException)
                    {
                        return false;
                    }
                case TypeKind.Bytes:
                    try
                    {
                        value = Convert.FromBase64String(text);
                        return true;
                    }
                    catch (FormatException)
                    {
                        return false;
                    }
                case TypeKind.Enum:
                    var parsed = ((EnumType)type).TryParse(text, out var member);
                    value = member;
                    return parsed;
                default:
                    return false;
            }
        }

        /// <summary>Text of a scalar, the inverse of <see cref="TryParse"/>.</summary>
        public static string Format(SerializableType type, object value) => type.Kind switch
        {
            TypeKind.Boolean => (bool)value ? "true" : "false",
            TypeKind.Int32 => ((int)value).ToString(CultureInfo.InvariantCulture),
            TypeKind.Int64 => ((long)value).ToString(CultureInfo.InvariantCulture),
            TypeKind.Double => ((double)value).ToString("R", CultureInfo.InvariantCulture),
            TypeKind.Decimal => ((decimal)value).ToString(CultureInfo.InvariantCulture),
            TypeKind.String => (string)value,
            TypeKind.Bytes => Convert.ToBase64String((byte[])value),
            TypeKind.Timestamp => FormatTimestamp(value),
            TypeKind.Guid => ((Guid)value).ToString("D"),
            TypeKind.Enum => ((EnumType)type).GetName(value),
            TypeKind.Duration => ((TimeSpan)value).ToString("c", CultureInfo.InvariantCulture),
            _ => throw new ArgumentException($"{type} is not a scalar type.", nameof(type))
        };

        /// <summary>Whether a value is the default of its type (0, false, "", empty list...): what <see cref="SerializationProfile.IgnoreDefaultValues"/> leaves out.</summary>
        public static bool IsDefault(SerializableType type, object? value) => value is null || type.Kind switch
        {
            TypeKind.Boolean => !(bool)value,
            TypeKind.Int32 => (int)value == 0,
            TypeKind.Int64 => (long)value == 0,
            // -0.0 is not the default: it must survive a round trip.
            TypeKind.Double => BitConverter.DoubleToInt64Bits((double)value) == 0,
            TypeKind.Decimal => (decimal)value == 0m,
            TypeKind.String => ((string)value).Length == 0,
            TypeKind.Bytes => ((byte[])value).Length == 0,
            TypeKind.Guid => (Guid)value == System.Guid.Empty,
            TypeKind.Enum => ((EnumType)type).ToNumber(value) == 0,
            TypeKind.Timestamp => ToTimestamp(value).UtcTicks == 0,
            TypeKind.Duration => (TimeSpan)value == TimeSpan.Zero,
            TypeKind.List => !((ListType)type).Enumerate(value).Any(),
            TypeKind.Map => !((MapType)type).Enumerate(value).Any(),
            _ => false
        };
    }
}

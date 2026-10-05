using Butterfly.Chrysalis.Http;
using Butterfly.Serialization;
using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Butterfly.Chrysalis.XmlRpc
{
    public static class XmlRpcHttpServerExtensions
    {
        /// <summary>
        /// Exposes the services of <paramref name="chrysalis"/> as XML-RPC at <paramref name="path"/>: the methodName of an
        /// operation is "Service.Operation", and its params are positional.
        /// </summary>
        public static HttpServer MapXmlRpc(this HttpServer http, ChrysalisServer chrysalis, string path = "/xmlrpc")
        {
            ArgumentNullException.ThrowIfNull(http);
            ArgumentNullException.ThrowIfNull(chrysalis);
            return http.Map(path, CreateHandler(chrysalis));
        }

        /// <summary>The XML-RPC handler, to host it elsewhere (ASP.NET Core).</summary>
        public static IHttpHandler CreateHandler(ChrysalisServer chrysalis)
        {
            ArgumentNullException.ThrowIfNull(chrysalis);
            return new XmlRpcHandler(chrysalis);
        }
    }

    /// <summary>
    /// XML-RPC (http://xmlrpc.com/spec.md). Values map as: bool to boolean, int to int, long to i8, double to double,
    /// decimal, Guid and enums to string, bytes to base64, timestamps to dateTime.iso8601 (UTC), messages to struct,
    /// lists to array, and missing values to nil. Faults carry the Chrysalis status as faultCode.
    /// </summary>
    internal sealed class XmlRpcHandler(ChrysalisServer chrysalis) : IHttpHandler
    {
        private SerializationProfile Profile => chrysalis.Options.Profile;

        public const string Protocol = "XML-RPC";
        private const int MaxDepth = 64;
        private const string DateFormat = "yyyyMMdd'T'HH:mm:ss";

        public async ValueTask HandleAsync(HttpServerContext http)
        {
            if (http.Request.Method != "POST")
            {
                http.Response.StatusCode = 405;
                http.Response.Headers["Allow"] = "POST";
                return;
            }

            XElement response;
            try
            {
                var call = Parse(http.Request.Body);
                var methodName = call.Element("methodName")?.Value.Trim()
                    ?? throw new ChrysalisException(ChrysalisStatus.InvalidArgument, "The call has no methodName.");
                var values = call.Element("params")?.Elements("param").Select(param => param.Element("value")
                    ?? throw new ChrysalisException(ChrysalisStatus.InvalidArgument, "A param has no value.")).ToList() ?? [];

                if (methodName == "system.listMethods")
                {
                    response = Success(new XElement("array", new XElement("data",
                        chrysalis.Services.SelectMany(service => service.Operations).Select(operation => Value(new XElement("string", operation.FullName))))));
                }
                else
                {
                    var operation = chrysalis.FindOperation(methodName)
                        ?? throw new ChrysalisException(ChrysalisStatus.Unimplemented, $"No method named '{methodName}'.");
                    if (values.Count > operation.Parameters.Count)
                        throw new ChrysalisException(ChrysalisStatus.InvalidArgument, $"{methodName} takes {operation.Parameters.Count} parameters.");

                    var arguments = operation.CreateDefaultArguments();
                    for (var i = 0; i < values.Count; i++)
                    {
                        var parameter = operation.Parameters[i];
                        arguments[i] = ReadValue(values[i], parameter.Type, parameter.Name, 0) ?? parameter.CreateDefault();
                    }

                    var context = ChrysalisHttp.CreateCallContext(operation, arguments, Protocol, http, http.RequestAborted);
                    var result = await chrysalis.InvokeAsync(context).ConfigureAwait(false);
                    // XML-RPC has no "nothing": operations without a result answer true.
                    response = Success(operation.ReturnType is null ? new XElement("boolean", "1") : WriteValue(operation.ReturnType, result, 0));
                }
            }
            catch (ChrysalisException exception)
            {
                response = new XElement("methodResponse", new XElement("fault", Value(new XElement("struct",
                    Member("faultCode", new XElement("int", (int)exception.Status)),
                    Member("faultString", new XElement("string", exception.Message))))));
            }

            http.Response.Write(Encoding.UTF8.GetBytes(new XDocument(new XDeclaration("1.0", "utf-8", null), response).Declaration + response.ToString(SaveOptions.DisableFormatting)));
            http.Response.Headers["Content-Type"] = "text/xml; charset=utf-8";
        }

        private static XElement Parse(ReadOnlyMemory<byte> body)
        {
            try
            {
                var settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = body.Length * 2L + 1024,
                    IgnoreComments = true
                };
                using var reader = XmlReader.Create(new MemoryStream(body.ToArray()), settings);
                var root = XDocument.Load(reader).Root;
                return root?.Name == "methodCall" ? root : throw new ChrysalisException(ChrysalisStatus.InvalidArgument, "The request is not a methodCall.");
            }
            catch (XmlException exception)
            {
                throw new ChrysalisException(ChrysalisStatus.InvalidArgument, $"The request is not well-formed XML: {exception.Message}");
            }
        }

        private static XElement Success(XElement value) => new("methodResponse", new XElement("params", new XElement("param", Value(value))));

        private static XElement Value(XElement content) => new("value", content);

        private static XElement Member(string name, XElement value) => new("member", new XElement("name", name), Value(value));

        private XElement WriteValue(SerializableType type, object? value, int depth)
        {
            if (value is null)
                return new XElement("nil");
            if (depth > MaxDepth)
                throw new ChrysalisException(ChrysalisStatus.Internal, $"The value is nested deeper than {MaxDepth} levels.");

            switch (type)
            {
                case ListType list:
                    return new XElement("array", new XElement("data", list.Enumerate(value).Select(item => Value(WriteValue(list.Element, item, depth + 1)))));
                case MapType map:
                    return new XElement("struct", map.Enumerate(value)
                        .Where(entry => entry.Value is not null || !Profile.IgnoreNullValues)
                        .Select(entry => Member(entry.Key, WriteValue(map.Value, entry.Value, depth + 1))));
                case ObjectType objectType:
                    return new XElement("struct", objectType.Members
                        .Where(member => member.IsVisibleIn(Profile))
                        .Select(member => (member, item: member.Get(value)))
                        .Where(pair => pair.item is not null || !Profile.IgnoreNullValues)
                        .Select(pair => Member(Profile.MemberName(pair.member, NamingPolicy.AsDeclared), WriteValue(pair.member.Type, pair.item, depth + 1))));
                case DynamicType dynamic:
                    return WriteDynamic(dynamic.ToValue(value), depth);
            }

            return type.Kind switch
            {
                TypeKind.Boolean => new XElement("boolean", (bool)value ? "1" : "0"),
                TypeKind.Int32 => new XElement("int", ((int)value).ToString(CultureInfo.InvariantCulture)),
                TypeKind.Int64 => new XElement("i8", ((long)value).ToString(CultureInfo.InvariantCulture)),
                TypeKind.Double => new XElement("double", XmlConvert.ToString((double)value)),
                TypeKind.Bytes => new XElement("base64", Convert.ToBase64String((byte[])value)),
                TypeKind.Timestamp => new XElement("dateTime.iso8601", ScalarText.ToTimestamp(value).UtcDateTime.ToString(DateFormat, CultureInfo.InvariantCulture)),
                TypeKind.Enum when !Profile.WritesEnumsAsNames(true) => new XElement("i8", ((EnumType)type).ToNumber(value).ToString(CultureInfo.InvariantCulture)),
                _ => new XElement("string", ScalarText.Format(type, value))
            };
        }

        // Dynamic values map to the natural XML-RPC types (decimals as double, the closest XML-RPC has).
        private static XElement WriteDynamic(SerializationValue value, int depth)
        {
            if (depth > MaxDepth)
                throw new ChrysalisException(ChrysalisStatus.Internal, $"The value is nested deeper than {MaxDepth} levels.");
            return value.Kind switch
            {
                ValueKind.Null => new XElement("nil"),
                ValueKind.Boolean => new XElement("boolean", value.AsBoolean() ? "1" : "0"),
                ValueKind.Integer => value.AsInt64() is >= int.MinValue and <= int.MaxValue ? new XElement("int", value.ToString()) : new XElement("i8", value.ToString()),
                ValueKind.Number or ValueKind.Decimal => new XElement("double", XmlConvert.ToString(value.AsDouble())),
                ValueKind.Bytes => new XElement("base64", Convert.ToBase64String(value.AsBytes())),
                ValueKind.Timestamp => new XElement("dateTime.iso8601", value.AsTimestamp().UtcDateTime.ToString(DateFormat, CultureInfo.InvariantCulture)),
                ValueKind.Array => new XElement("array", new XElement("data", value.Items.Select(item => Value(WriteDynamic(item, depth + 1))))),
                ValueKind.Object => new XElement("struct", value.Members.Select(member => Member(member.Key, WriteDynamic(member.Value, depth + 1)))),
                _ => new XElement("string", value.AsString())
            };
        }

        private static SerializationValue ReadDynamic(XElement value, string path, int depth)
        {
            if (depth > MaxDepth)
                throw Invalid(path, $"nested deeper than {MaxDepth} levels.");
            var typed = value.Elements().FirstOrDefault();
            var tag = typed?.Name.LocalName ?? "string";
            var text = (typed ?? value).Value.Trim();
            try
            {
                return tag switch
                {
                    "nil" => SerializationValue.Null,
                    "boolean" => SerializationValue.From(text == "1"),
                    "int" or "i4" or "i8" => SerializationValue.From(long.Parse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture)),
                    "double" => SerializationValue.From(double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture)),
                    "base64" => SerializationValue.From(Convert.FromBase64String(text)),
                    "dateTime.iso8601" => SerializationValue.From(ParseDate(text, path)),
                    "array" => SerializationValue.Array((typed!.Element("data")?.Elements("value") ?? []).Select((item, index) => ReadDynamic(item, SerializationException.Item(path, index), depth + 1))),
                    "struct" => SerializationValue.Object(typed!.Elements("member").Select(member => new KeyValuePair<string, SerializationValue>(
                        member.Element("name")?.Value ?? "", member.Element("value") is { } memberValue ? ReadDynamic(memberValue, SerializationException.Child(path, member.Element("name")?.Value ?? ""), depth + 1) : SerializationValue.Null))),
                    _ => SerializationValue.From(typed is null ? value.Value : typed.Value)
                };
            }
            catch (Exception exception) when (exception is FormatException or OverflowException)
            {
                throw Invalid(path, $"'{text}' is not a valid {tag}.");
            }
        }

        private object? ReadValue(XElement value, SerializableType expected, string path, int depth)
        {
            if (depth > MaxDepth)
                throw Invalid(path, $"nested deeper than {MaxDepth} levels.");

            // A value without a type element is a string.
            var typed = value.Elements().FirstOrDefault();
            var tag = typed?.Name.LocalName ?? "string";
            var text = typed is null ? value.Value : typed.Value;

            if (tag == "nil")
                return null;

            switch (expected)
            {
                case DynamicType dynamic:
                    return dynamic.FromValue(ReadDynamic(value, path, depth));

                case ObjectType objectType:
                    if (tag != "struct")
                        throw Invalid(path, "expected a struct.");
                    var values = objectType.CreateDefaultValues();
                    foreach (var member in typed!.Elements("member"))
                    {
                        var name = member.Element("name")?.Value;
                        if (name is null || objectType.FindMember(name, Profile, NamingPolicy.AsDeclared) is not { } target || member.Element("value") is not { } memberValue)
                            continue;
                        // Members the profile hides cannot be set from the request.
                        if (!target.IsVisibleIn(Profile))
                            continue;
                        values[target.Index] = ReadValue(memberValue, target.Type, SerializationException.Child(path, name), depth + 1) ?? target.CreateDefault();
                    }
                    return objectType.Create(values);

                case MapType map:
                    if (tag != "struct")
                        throw Invalid(path, "expected a struct.");
                    return map.Create([.. typed!.Elements("member").Select(member =>
                    {
                        var key = member.Element("name")?.Value ?? "";
                        var entry = member.Element("value") is { } entryValue ? ReadValue(entryValue, map.Value, SerializationException.Child(path, key), depth + 1) : null;
                        return new KeyValuePair<string, object?>(key, entry ?? (map.ValueIsOptional ? null : map.Value.CreateDefault()));
                    })]);

                case ListType list:
                    if (tag != "array")
                        throw Invalid(path, "expected an array.");
                    var items = typed!.Element("data")?.Elements("value")
                        .Select((item, index) => ReadValue(item, list.Element, SerializationException.Item(path, index), depth + 1) ?? list.Element.CreateDefault())
                        .ToList() ?? [];
                    return list.Create(items);
            }

            switch (expected.Kind)
            {
                case TypeKind.Bytes when tag == "base64":
                    try
                    {
                        return Convert.FromBase64String(text.Trim());
                    }
                    catch (FormatException)
                    {
                        throw Invalid(path, "invalid base64.");
                    }

                case TypeKind.Timestamp when tag == "dateTime.iso8601":
                    return ScalarText.FromTimestamp(expected, ParseDate(text.Trim(), path));

                case TypeKind.Double when tag is "double" or "int" or "i4" or "i8":
                    return double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : throw Invalid(path, $"'{text}' is not a number.");

                default:
                    if (tag is "struct" or "array")
                        throw Invalid(path, $"expected a {expected}, not a {tag}.");
                    var scalarText = expected.Kind == TypeKind.String ? text : text.Trim();
                    return ScalarText.TryParse(expected, scalarText, out var scalar)
                        ? scalar
                        : throw Invalid(path, $"'{text}' is not a valid {expected}.");
            }
        }

        // The specification's form is 19980717T14:08:55; ISO 8601 with dashes and offsets is common too.
        private static DateTimeOffset ParseDate(string text, string path)
        {
            if (DateTime.TryParseExact(text, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var compact))
                return new DateTimeOffset(compact, TimeSpan.Zero);
            return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : throw Invalid(path, $"'{text}' is not a valid date.");
        }

        private static ChrysalisException Invalid(string path, string problem) =>
            new(ChrysalisStatus.InvalidArgument, path.Length == 0 ? problem : $"'{path}': {problem}");
    }
}
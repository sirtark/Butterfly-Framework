using System.Buffers;
using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Butterfly.Serialization.Xml
{
    /// <summary>
    /// XML in the shape XML Schema describes (and SOAP uses): an object is a sequence of elements named after its members,
    /// a list repeats its element, a missing optional value is an absent element, and a map is a list of
    /// &lt;entry key="..."&gt;. Dynamic values carry a type="..." attribute so they read back with their kind. Names are as
    /// declared by default. Parsing never processes DTDs (no entity expansion, no external resolution).
    /// </summary>
    public sealed class XmlFormat : SerializationFormat
    {
        private const string TypeAttribute = "type";
        private const string KeyAttribute = "key";

        public XmlFormat(XNamespace? @namespace = null)
        {
            Namespace = @namespace ?? XNamespace.None;
        }

        public static XmlFormat Instance { get; } = new();

        /// <summary>Namespace of every element.</summary>
        public XNamespace Namespace { get; }

        public override string Name => "xml";
        public override string MediaType => "application/xml";
        public override bool IsText => true;

        /// <summary>Settings that keep XML parsing safe: no DTD (entity expansion, XXE), no external resolution.</summary>
        public static XmlReaderSettings ReaderSettings(long maxCharacters) => new()
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = maxCharacters,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true
        };

        public override void Write(IBufferWriter<byte> output, SerializableType type, object? value, SerializationProfile profile)
        {
            var root = new XElement(Namespace + RootName(type));
            if (type is ListType list)
            {
                if (value is not null)
                {
                    foreach (var item in list.Enumerate(value))
                        WriteMember(root, Namespace + "item", list.Element, item, profile);
                }
            }
            else
            {
                WriteContent(root, type, value, profile, 0);
            }

            var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = profile.Indented, OmitXmlDeclaration = false };
            using var stream = new MemoryStream();
            using (var writer = XmlWriter.Create(stream, settings))
                new XDocument(root).Save(writer);
            output.Write(stream.ToArray());
        }

        public override object? Read(ReadOnlyMemory<byte> input, SerializableType type, SerializationProfile profile)
        {
            XElement root;
            try
            {
                using var reader = XmlReader.Create(new MemoryStream(input.ToArray()), ReaderSettings(input.Length * 2L + 1024));
                root = XDocument.Load(reader).Root ?? throw new SerializationException("", "the document has no root element.");
            }
            catch (XmlException exception)
            {
                throw new SerializationException("", $"invalid XML: {exception.Message}", exception);
            }

            if (type is ListType list)
                return ReadMember(root, "item", list, optional: false, profile, "");
            return ReadElement(root, type, profile, "") ?? type.CreateDefault();
        }

        /// <summary>Adds the element(s) of a value to <paramref name="parent"/>: one element, one per item for a list, none for null.</summary>
        public void WriteMember(XElement parent, XName name, SerializableType type, object? value, SerializationProfile profile) =>
            WriteMember(parent, name, type, value, profile, 0);

        /// <summary>Reads the value of the children of <paramref name="parent"/> named <paramref name="localName"/> (all of them for a list).</summary>
        public object? ReadMember(XElement parent, string localName, SerializableType type, bool optional, SerializationProfile profile, string path) =>
            ReadMember(parent, localName, type, optional, profile, path, 0);

        public object? ReadElement(XElement element, SerializableType type, SerializationProfile profile, string path) =>
            ReadElement(element, type, profile, path, 0);

        /// <summary>The element name of a member under a profile (declared names unless the profile says otherwise).</summary>
        public static string MemberName(SerializableMember member, SerializationProfile profile) =>
            XmlConvert.EncodeLocalName(profile.MemberName(member, NamingPolicy.AsDeclared));

        private void WriteMember(XElement parent, XName name, SerializableType type, object? value, SerializationProfile profile, int depth)
        {
            if (value is null)
                return;
            if (type is ListType list)
            {
                foreach (var item in list.Enumerate(value))
                    WriteMember(parent, name, list.Element, item ?? list.Element.CreateDefault(), profile, depth + 1);
                return;
            }

            var element = new XElement(name);
            WriteContent(element, type, value, profile, depth);
            parent.Add(element);
        }

        private void WriteContent(XElement element, SerializableType type, object? value, SerializationProfile profile, int depth)
        {
            if (value is null)
                return;
            if (depth > profile.MaxDepth)
                throw new SerializationException("", $"the value is nested deeper than {profile.MaxDepth} levels.");

            switch (type)
            {
                case ObjectType objectType:
                    foreach (var member in objectType.Members)
                    {
                        if (!member.IsVisibleIn(profile))
                            continue;
                        var memberValue = member.Get(value);
                        if (profile.IgnoreDefaultValues && ScalarText.IsDefault(member.Type, memberValue))
                            continue;
                        WriteMember(element, element.Name.Namespace + MemberName(member, profile), member.Type, memberValue, profile, depth + 1);
                    }
                    break;
                case MapType map:
                    foreach (var (key, entryValue) in map.Enumerate(value))
                    {
                        if (entryValue is null && profile.IgnoreNullValues)
                            continue;
                        var entry = new XElement(element.Name.Namespace + "entry", new XAttribute(KeyAttribute, key));
                        WriteContent(entry, map.Value, entryValue, profile, depth + 1);
                        element.Add(entry);
                    }
                    break;
                case ListType list:
                    foreach (var item in list.Enumerate(value))
                        WriteMember(element, element.Name.Namespace + "item", list.Element, item, profile, depth + 1);
                    break;
                case DynamicType dynamic:
                    WriteDynamic(element, dynamic.ToValue(value));
                    break;
                default:
                    element.Value = FormatScalar(type, value, profile);
                    break;
            }
        }

        private static void WriteDynamic(XElement element, SerializationValue value)
        {
            var kind = value.Kind switch
            {
                ValueKind.Null => "null",
                ValueKind.Boolean => "boolean",
                ValueKind.Integer => "integer",
                ValueKind.Number => "number",
                ValueKind.Decimal => "decimal",
                ValueKind.Bytes => "bytes",
                ValueKind.Timestamp => "timestamp",
                ValueKind.Array => "array",
                ValueKind.Object => "object",
                _ => null
            };
            if (kind is not null)
                element.SetAttributeValue(TypeAttribute, kind);

            switch (value.Kind)
            {
                case ValueKind.Null:
                    break;
                case ValueKind.Array:
                    foreach (var item in value.Items)
                    {
                        var child = new XElement(element.Name.Namespace + "item");
                        WriteDynamic(child, item);
                        element.Add(child);
                    }
                    break;
                case ValueKind.Object:
                    foreach (var (name, member) in value.Members)
                    {
                        var child = new XElement(element.Name.Namespace + XmlConvert.EncodeLocalName(name));
                        WriteDynamic(child, member);
                        element.Add(child);
                    }
                    break;
                case ValueKind.Number:
                    element.Value = XmlConvert.ToString(value.AsDouble());
                    break;
                case ValueKind.Bytes:
                    element.Value = Convert.ToBase64String(value.AsBytes());
                    break;
                case ValueKind.Timestamp:
                    element.Value = ScalarText.FormatTimestamp(value.AsTimestamp());
                    break;
                default:
                    element.Value = value.Kind == ValueKind.String ? value.AsString() : value.ToString();
                    break;
            }
        }

        private object? ReadMember(XElement parent, string localName, SerializableType type, bool optional, SerializationProfile profile, string path, int depth)
        {
            var elements = parent.Elements().Where(element => element.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (type is ListType list)
                return list.Create([.. elements.Select((element, index) => ReadElement(element, list.Element, profile, SerializationException.Item(path, index), depth + 1) ?? list.Element.CreateDefault())]);
            if (elements.Count == 0)
                return optional ? null : type.CreateDefault();
            return ReadElement(elements[^1], type, profile, path, depth) ?? (optional ? null : type.CreateDefault());
        }

        private object? ReadElement(XElement element, SerializableType type, SerializationProfile profile, string path, int depth)
        {
            if (depth > profile.MaxDepth)
                throw new SerializationException(path, $"nested deeper than {profile.MaxDepth} levels.");

            switch (type)
            {
                case ObjectType objectType:
                    var values = objectType.CreateDefaultValues();
                    foreach (var member in objectType.Members)
                    {
                        if (!member.IsVisibleIn(profile))
                            continue;
                        var name = MemberName(member, profile);
                        // The declared name is accepted too, whatever the profile's naming.
                        var lookup = element.Elements().Any(child => child.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase)) ? name : member.Name;
                        values[member.Index] = ReadMember(element, lookup, member.Type, member.IsOptional, profile, SerializationException.Child(path, name), depth + 1);
                    }
                    if (profile.RejectUnknownMembers)
                    {
                        foreach (var child in element.Elements())
                        {
                            if (objectType.FindMember(XmlConvert.DecodeName(child.Name.LocalName), profile, NamingPolicy.AsDeclared) is null)
                                throw new SerializationException(SerializationException.Child(path, child.Name.LocalName), "unknown member.");
                        }
                    }
                    return objectType.Create(values);

                case MapType map:
                    return map.Create([.. element.Elements().Where(child => child.Name.LocalName == "entry").Select(entry =>
                    {
                        var key = (string?)entry.Attribute(KeyAttribute) ?? throw new SerializationException(path, "a map entry has no key attribute.");
                        var entryValue = ReadElement(entry, map.Value, profile, SerializationException.Child(path, key), depth + 1);
                        return new KeyValuePair<string, object?>(key, entryValue ?? (map.ValueIsOptional ? null : map.Value.CreateDefault()));
                    })]);

                case ListType list:
                    return ReadMember(element, "item", list, optional: false, profile, path, depth);

                case DynamicType dynamic:
                    return dynamic.FromValue(ReadDynamic(element, path));

                default:
                    return ParseScalar(type, element.Value, path);
            }
        }

        private static SerializationValue ReadDynamic(XElement element, string path)
        {
            var kind = (string?)element.Attribute(TypeAttribute);
            var text = element.Value;
            try
            {
                return kind switch
                {
                    "null" => SerializationValue.Null,
                    "boolean" => SerializationValue.From(XmlConvert.ToBoolean(text.Trim())),
                    "integer" => SerializationValue.From(long.Parse(text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture)),
                    "number" => SerializationValue.From(XmlConvert.ToDouble(text.Trim())),
                    "decimal" => SerializationValue.From(decimal.Parse(text.Trim(), NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture)),
                    "bytes" => SerializationValue.From(Convert.FromBase64String(text.Trim())),
                    "timestamp" => SerializationValue.From(DateTimeOffset.Parse(text.Trim(), CultureInfo.InvariantCulture)),
                    "array" => SerializationValue.Array(element.Elements().Select((item, index) => ReadDynamic(item, SerializationException.Item(path, index)))),
                    "object" => SerializationValue.Object(element.Elements().Select(child => new KeyValuePair<string, SerializationValue>(
                        XmlConvert.DecodeName(child.Name.LocalName), ReadDynamic(child, SerializationException.Child(path, child.Name.LocalName))))),
                    // Untyped: an element with children is an object, otherwise text.
                    _ => element.HasElements
                        ? SerializationValue.Object(element.Elements().Select(child => new KeyValuePair<string, SerializationValue>(
                            XmlConvert.DecodeName(child.Name.LocalName), ReadDynamic(child, SerializationException.Child(path, child.Name.LocalName)))))
                        : SerializationValue.From(text)
                };
            }
            catch (FormatException exception)
            {
                throw new SerializationException(path, $"'{text}' is not a valid {kind}.", exception);
            }
            catch (OverflowException exception)
            {
                throw new SerializationException(path, $"'{text}' is out of range.", exception);
            }
        }

        public static string FormatScalar(SerializableType type, object value, SerializationProfile profile) => type.Kind switch
        {
            TypeKind.Double => XmlConvert.ToString((double)value),
            TypeKind.Decimal => XmlConvert.ToString((decimal)value),
            TypeKind.Enum when !profile.WritesEnumsAsNames(true) => ((EnumType)type).ToNumber(value).ToString(CultureInfo.InvariantCulture),
            _ => ScalarText.Format(type, value)
        };

        public static object? ParseScalar(SerializableType type, string text, string path)
        {
            if (type.Kind == TypeKind.Double)
            {
                try
                {
                    return XmlConvert.ToDouble(text.Trim());
                }
                catch (FormatException)
                {
                    // Falls back to the common forms ("Infinity", "NaN").
                }
            }
            return ScalarText.TryParse(type, type.Kind == TypeKind.String ? text : text.Trim(), out var value)
                ? value
                : throw new SerializationException(path, $"'{text}' is not a valid {type}.");
        }

        private static string RootName(SerializableType type) => XmlConvert.EncodeLocalName(type switch
        {
            ObjectType objectType => objectType.Name,
            ListType => "List",
            MapType => "Map",
            _ => "Value"
        });
    }
}

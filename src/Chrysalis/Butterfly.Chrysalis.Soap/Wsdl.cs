using Butterfly.Serialization;
using Butterfly.Serialization.Xml;
using System.Xml.Linq;

namespace Butterfly.Chrysalis.Soap
{
    /// <summary>
    /// WSDL 1.1 (document/literal wrapped) of a service, with SOAP 1.1 and 1.2 bindings. The schema describes what the
    /// profile writes: its visible members, their names and the enum format.
    /// </summary>
    public static class Wsdl
    {
        private static readonly XNamespace WsdlNs = "http://schemas.xmlsoap.org/wsdl/";
        private static readonly XNamespace Soap11Binding = "http://schemas.xmlsoap.org/wsdl/soap/";
        private static readonly XNamespace Soap12Binding = "http://schemas.xmlsoap.org/wsdl/soap12/";
        private static readonly XNamespace Xs = "http://www.w3.org/2001/XMLSchema";
        private const string HttpTransport = "http://schemas.xmlsoap.org/soap/http";

        public static XDocument Generate(ChrysalisService service, string location, SerializationProfile? profile = null)
        {
            ArgumentNullException.ThrowIfNull(service);
            profile ??= SerializationProfile.Default;
            XNamespace tns = SoapHandler.TargetNamespace(service);

            var schema = new XElement(Xs + "schema",
                new XAttribute("targetNamespace", tns.NamespaceName),
                new XAttribute("elementFormDefault", "qualified"));
            schema.Add(new XElement(Xs + "simpleType", new XAttribute("name", "guid"),
                new XElement(Xs + "restriction", new XAttribute("base", "xs:string"),
                    new XElement(Xs + "pattern", new XAttribute("value", "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")))));

            var described = new HashSet<SerializableType>();
            foreach (var operation in service.Operations)
            {
                schema.Add(new XElement(Xs + "element", new XAttribute("name", operation.Name),
                    new XElement(Xs + "complexType", new XElement(Xs + "sequence",
                        operation.Parameters.Select(parameter => Element(parameter.Name, parameter.Type, parameter.IsOptional, profile))))));
                schema.Add(new XElement(Xs + "element", new XAttribute("name", operation.Name + "Response"),
                    new XElement(Xs + "complexType", new XElement(Xs + "sequence",
                        operation.ReturnType is null ? null : Element(operation.Name + "Result", operation.ReturnType, optional: true, profile)))));

                foreach (var parameter in operation.Parameters)
                    Describe(schema, parameter.Type, described, profile);
                if (operation.ReturnType is not null)
                    Describe(schema, operation.ReturnType, described, profile);
            }

            var definitions = new XElement(WsdlNs + "definitions",
                new XAttribute("name", service.Name),
                new XAttribute("targetNamespace", tns.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "wsdl", WsdlNs),
                new XAttribute(XNamespace.Xmlns + "soap", Soap11Binding),
                new XAttribute(XNamespace.Xmlns + "soap12", Soap12Binding),
                new XAttribute(XNamespace.Xmlns + "xs", Xs),
                new XAttribute(XNamespace.Xmlns + "tns", tns),
                new XElement(WsdlNs + "types", schema));

            foreach (var operation in service.Operations)
            {
                definitions.Add(new XElement(WsdlNs + "message", new XAttribute("name", operation.Name + "Request"),
                    new XElement(WsdlNs + "part", new XAttribute("name", "parameters"), new XAttribute("element", "tns:" + operation.Name))));
                definitions.Add(new XElement(WsdlNs + "message", new XAttribute("name", operation.Name + "Response"),
                    new XElement(WsdlNs + "part", new XAttribute("name", "parameters"), new XAttribute("element", "tns:" + operation.Name + "Response"))));
            }

            definitions.Add(new XElement(WsdlNs + "portType", new XAttribute("name", service.Name + "PortType"),
                service.Operations.Select(operation => new XElement(WsdlNs + "operation", new XAttribute("name", operation.Name),
                    new XElement(WsdlNs + "input", new XAttribute("message", "tns:" + operation.Name + "Request")),
                    new XElement(WsdlNs + "output", new XAttribute("message", "tns:" + operation.Name + "Response"))))));

            definitions.Add(Binding(service, tns, Soap11Binding, "Soap11"));
            definitions.Add(Binding(service, tns, Soap12Binding, "Soap12"));

            definitions.Add(new XElement(WsdlNs + "service", new XAttribute("name", service.Name),
                new XElement(WsdlNs + "port", new XAttribute("name", service.Name + "Soap11"), new XAttribute("binding", "tns:" + service.Name + "Soap11"),
                    new XElement(Soap11Binding + "address", new XAttribute("location", location))),
                new XElement(WsdlNs + "port", new XAttribute("name", service.Name + "Soap12"), new XAttribute("binding", "tns:" + service.Name + "Soap12"),
                    new XElement(Soap12Binding + "address", new XAttribute("location", location)))));

            return new XDocument(definitions);
        }

        private static XElement Binding(ChrysalisService service, XNamespace tns, XNamespace soap, string suffix) =>
            new(WsdlNs + "binding", new XAttribute("name", service.Name + suffix), new XAttribute("type", "tns:" + service.Name + "PortType"),
                new XElement(soap + "binding", new XAttribute("transport", HttpTransport), new XAttribute("style", "document")),
                service.Operations.Select(operation => new XElement(WsdlNs + "operation", new XAttribute("name", operation.Name),
                    new XElement(soap + "operation", new XAttribute("soapAction", tns.NamespaceName + "/" + operation.Name), new XAttribute("style", "document")),
                    new XElement(WsdlNs + "input", new XElement(soap + "body", new XAttribute("use", "literal"))),
                    new XElement(WsdlNs + "output", new XElement(soap + "body", new XAttribute("use", "literal"))))));

        private static XElement Element(string name, SerializableType type, bool optional, SerializationProfile profile)
        {
            var element = new XElement(Xs + "element", new XAttribute("name", name));
            if (type is MapType map)
            {
                // <entry key="...">value</entry> repeated; the value is described loosely (it may be any kind).
                element.Add(new XElement(Xs + "complexType", new XElement(Xs + "sequence",
                    new XElement(Xs + "element", new XAttribute("name", "entry"), new XAttribute("minOccurs", 0), new XAttribute("maxOccurs", "unbounded"),
                        new XElement(Xs + "complexType", new XAttribute("mixed", "true"),
                            new XElement(Xs + "sequence", new XElement(Xs + "any", new XAttribute("processContents", "lax"), new XAttribute("minOccurs", 0), new XAttribute("maxOccurs", "unbounded"))),
                            new XElement(Xs + "attribute", new XAttribute("name", "key"), new XAttribute("type", "xs:string"), new XAttribute("use", "required")))))));
            }
            else
            {
                element.Add(new XAttribute("type", SchemaType(type, profile)));
            }

            if (type is ListType)
                element.Add(new XAttribute("minOccurs", 0), new XAttribute("maxOccurs", "unbounded"));
            else if (optional || type is ObjectType or MapType or DynamicType)
                element.Add(new XAttribute("minOccurs", 0));
            return element;
        }

        private static string SchemaType(SerializableType type, SerializationProfile profile) => type.Kind switch
        {
            TypeKind.Boolean => "xs:boolean",
            TypeKind.Int32 => "xs:int",
            TypeKind.Int64 => "xs:long",
            TypeKind.Double => "xs:double",
            TypeKind.Decimal => "xs:decimal",
            TypeKind.String => "xs:string",
            TypeKind.Bytes => "xs:base64Binary",
            TypeKind.Timestamp => "xs:dateTime",
            TypeKind.Guid => "tns:guid",
            TypeKind.Dynamic => "xs:anyType",
            TypeKind.Duration => "xs:string",
            TypeKind.Enum when !profile.WritesEnumsAsNames(true) => "xs:long",
            TypeKind.List => SchemaType(((ListType)type).Element, profile),
            _ => "tns:" + type.Name
        };

        private static void Describe(XElement schema, SerializableType type, HashSet<SerializableType> described, SerializationProfile profile)
        {
            switch (type)
            {
                case ListType list:
                    Describe(schema, list.Element, described, profile);
                    return;
                case MapType map:
                    Describe(schema, map.Value, described, profile);
                    return;
            }
            if (!described.Add(type))
                return;

            if (type is EnumType enumeration && profile.WritesEnumsAsNames(true))
            {
                schema.Add(new XElement(Xs + "simpleType", new XAttribute("name", type.Name),
                    new XElement(Xs + "restriction", new XAttribute("base", "xs:string"),
                        enumeration.Members.Select(member => new XElement(Xs + "enumeration", new XAttribute("value", member.Name))))));
            }
            else if (type is ObjectType objectType)
            {
                var members = objectType.Members.Where(member => member.IsVisibleIn(profile)).ToList();
                schema.Add(new XElement(Xs + "complexType", new XAttribute("name", type.Name),
                    new XElement(Xs + "sequence", members.Select(member => Element(XmlFormat.MemberName(member, profile), member.Type, member.IsOptional, profile)))));
                foreach (var member in members)
                    Describe(schema, member.Type, described, profile);
            }
        }
    }
}
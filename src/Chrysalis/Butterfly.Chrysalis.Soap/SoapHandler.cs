using Butterfly.Chrysalis.Http;
using Butterfly.Serialization;
using Butterfly.Serialization.Xml;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Butterfly.Chrysalis.Soap
{
    public static class SoapHttpServerExtensions
    {
        /// <summary>
        /// Exposes every service of <paramref name="chrysalis"/> as SOAP 1.1 and 1.2 at {prefix}/{service}; its WSDL is
        /// served at {prefix}/{service}?wsdl.
        /// </summary>
        public static HttpServer MapSoap(this HttpServer http, ChrysalisServer chrysalis, string pathPrefix = "/soap")
        {
            ArgumentNullException.ThrowIfNull(http);
            ArgumentNullException.ThrowIfNull(chrysalis);
            return http.Map(pathPrefix, CreateHandler(chrysalis, pathPrefix));
        }

        /// <summary>The SOAP handler, to host it elsewhere (ASP.NET Core): it serves {prefix}/{service}.</summary>
        public static IHttpHandler CreateHandler(ChrysalisServer chrysalis, string pathPrefix = "/soap")
        {
            ArgumentNullException.ThrowIfNull(chrysalis);
            return new SoapHandler(chrysalis, pathPrefix);
        }
    }

    internal enum SoapVersion { Soap11, Soap12 }

    internal sealed class SoapHandler(ChrysalisServer chrysalis, string pathPrefix) : IHttpHandler
    {
        public const string Protocol = "SOAP";
        public static readonly XNamespace Soap11 = "http://schemas.xmlsoap.org/soap/envelope/";
        public static readonly XNamespace Soap12 = "http://www.w3.org/2003/05/soap-envelope";
        public static readonly XNamespace ChrysalisNamespace = "urn:butterfly:chrysalis";

        private readonly string prefix = "/" + pathPrefix.Trim('/');

        public static string TargetNamespace(ChrysalisService service) => $"urn:{service.Namespace}:{service.Name}";

        public async ValueTask HandleAsync(HttpServerContext http)
        {
            var request = http.Request;
            var serviceName = request.Path.Length > prefix.Length ? request.Path[prefix.Length..].Trim('/') : "";
            var service = chrysalis.FindService(serviceName);
            if (service is null)
            {
                http.Response.StatusCode = 404;
                http.Response.Write($"No SOAP service named '{serviceName}'.", "text/plain; charset=utf-8");
                return;
            }

            if (request.Method == "GET" && request.Query.Contains("wsdl"))
            {
                var location = ChrysalisHttp.BaseUrl(request) + prefix + "/" + service.Name;
                http.Response.Write(Wsdl.Generate(service, location, chrysalis.Options.Profile).ToString(SaveOptions.DisableFormatting), "text/xml; charset=utf-8");
                return;
            }
            if (request.Method != "POST")
            {
                http.Response.StatusCode = 405;
                http.Response.Headers["Allow"] = "GET, POST";
                return;
            }

            var version = request.MediaType switch
            {
                "text/xml" => SoapVersion.Soap11,
                "application/soap+xml" => SoapVersion.Soap12,
                _ => (SoapVersion?)null
            };
            if (version is null)
            {
                http.Response.StatusCode = 415;
                http.Response.Write("SOAP requests must be text/xml (SOAP 1.1) or application/soap+xml (SOAP 1.2).", "text/plain; charset=utf-8");
                return;
            }

            try
            {
                var (operation, arguments) = ReadRequest(service, request, version.Value, chrysalis.Options.Profile);
                var context = ChrysalisHttp.CreateCallContext(operation, arguments, Protocol, http, http.RequestAborted);
                var result = await chrysalis.InvokeAsync(context).ConfigureAwait(false);

                XNamespace tns = TargetNamespace(service);
                var response = new XElement(tns + (operation.Name + "Response"));
                if (operation.ReturnType is not null)
                    XmlFormat.Instance.WriteMember(response, tns + (operation.Name + "Result"), operation.ReturnType, result, chrysalis.Options.Profile);
                WriteEnvelope(http.Response, version.Value, response, 200);
            }
            catch (SoapFault fault)
            {
                WriteFault(http.Response, version.Value, fault.Code, fault.Message, null);
            }
            catch (ChrysalisException exception)
            {
                WriteFault(http.Response, version.Value, exception.IsClientError ? "Client" : "Server", exception.Message, exception.Status);
            }
            catch (SerializationException exception)
            {
                // Only results reach here (arguments are translated while reading): a server-side problem.
                WriteFault(http.Response, version.Value, "Server", exception.Message, ChrysalisStatus.Internal);
            }
        }

        private static (ChrysalisOperation Operation, object?[] Arguments) ReadRequest(ChrysalisService service, HttpServerRequest request, SoapVersion version, SerializationProfile profile)
        {
            XDocument document;
            try
            {
                using var reader = XmlReader.Create(new MemoryStream(request.Body.ToArray()), XmlFormat.ReaderSettings(request.Body.Length * 2L + 1024));
                document = XDocument.Load(reader);
            }
            catch (XmlException exception)
            {
                throw new SoapFault("Client", $"The request is not well-formed XML: {exception.Message}");
            }

            var soap = version == SoapVersion.Soap11 ? Soap11 : Soap12;
            var envelope = document.Root;
            if (envelope is null || envelope.Name.LocalName != "Envelope")
                throw new SoapFault("Client", "The request is not a SOAP envelope.");
            if (envelope.Name.Namespace != soap)
                throw new SoapFault("VersionMismatch", "The envelope namespace does not match the SOAP version of the Content-Type.");

            // Headers that must be understood cannot be: Chrysalis processes none (SOAP 1.1 4.2.3, SOAP 1.2 5.2.3).
            var mustUnderstand = envelope.Element(soap + "Header")?.Elements()
                .FirstOrDefault(header => (string?)header.Attribute(soap + "mustUnderstand") is "1" or "true");
            if (mustUnderstand is not null)
                throw new SoapFault("MustUnderstand", $"The header {mustUnderstand.Name} is not understood.");

            var call = envelope.Element(soap + "Body")?.Elements().FirstOrDefault()
                ?? throw new SoapFault("Client", "The SOAP body is empty.");
            var operation = service.FindOperation(call.Name.LocalName)
                ?? throw new ChrysalisException(ChrysalisStatus.Unimplemented, $"The service {service.Name} has no operation {call.Name.LocalName}.");

            var arguments = new object?[operation.Parameters.Count];
            for (var i = 0; i < arguments.Length; i++)
            {
                var parameter = operation.Parameters[i];
                try
                {
                    arguments[i] = XmlFormat.Instance.ReadMember(call, parameter.Name, parameter.Type, parameter.IsOptional, profile, parameter.Name);
                }
                catch (SerializationException exception)
                {
                    throw ChrysalisException.InvalidInput(exception);
                }
            }
            return (operation, arguments);
        }

        private static void WriteEnvelope(HttpServerResponse response, SoapVersion version, XElement body, int status)
        {
            var soap = version == SoapVersion.Soap11 ? Soap11 : Soap12;
            var envelope = new XElement(soap + "Envelope",
                new XAttribute(XNamespace.Xmlns + "soap", soap),
                new XElement(soap + "Body", body));

            response.Reset();
            response.StatusCode = status;
            var contentType = version == SoapVersion.Soap11 ? "text/xml; charset=utf-8" : "application/soap+xml; charset=utf-8";
            response.Write(Encoding.UTF8.GetBytes(envelope.ToString(SaveOptions.DisableFormatting)));
            response.Headers["Content-Type"] = contentType;
        }

        // code: "Client"/"Server" (SOAP 1.1 names, mapped to Sender/Receiver for 1.2), "VersionMismatch" or "MustUnderstand".
        private static void WriteFault(HttpServerResponse response, SoapVersion version, string code, string message, ChrysalisStatus? status)
        {
            var detail = status is null ? null : new XElement(ChrysalisNamespace + "status", new XAttribute(XNamespace.Xmlns + "chrysalis", ChrysalisNamespace), status.Value.ToString());
            XElement fault;
            int httpStatus;

            if (version == SoapVersion.Soap11)
            {
                fault = new XElement(Soap11 + "Fault",
                    new XElement("faultcode", "soap:" + code),
                    new XElement("faultstring", message),
                    detail is null ? null : new XElement("detail", detail));
                httpStatus = 500;
            }
            else
            {
                var value = code switch { "Client" => "Sender", "Server" => "Receiver", _ => code };
                fault = new XElement(Soap12 + "Fault",
                    new XElement(Soap12 + "Code", new XElement(Soap12 + "Value", "soap:" + value)),
                    new XElement(Soap12 + "Reason", new XElement(Soap12 + "Text", new XAttribute(XNamespace.Xml + "lang", "en"), message)),
                    detail is null ? null : new XElement(Soap12 + "Detail", detail));
                httpStatus = value == "Sender" ? 400 : 500;
            }
            WriteEnvelope(response, version, fault, httpStatus);
        }

        private sealed class SoapFault(string code, string message) : Exception(message)
        {
            public string Code { get; } = code;
        }
    }
}

using Butterfly.Chrysalis.Http;
using Butterfly.Serialization;
using Butterfly.Serialization.Json;
using System.Text.Json;

namespace Butterfly.Chrysalis.Rest
{
    public static class RestHttpServerExtensions
    {
        /// <summary>
        /// Exposes the services of <paramref name="chrysalis"/> as REST under <paramref name="pathPrefix"/>: an operation with
        /// [HttpGet("products/{id}")] answers GET {prefix}/{service route}/products/{id}; one without an attribute answers
        /// POST {prefix}/{service route}/{operation} with its parameters in a JSON object.
        /// </summary>
        public static HttpServer MapRest(this HttpServer http, ChrysalisServer chrysalis, string pathPrefix = "/api")
        {
            ArgumentNullException.ThrowIfNull(http);
            ArgumentNullException.ThrowIfNull(chrysalis);
            return http.Map(pathPrefix, CreateHandler(chrysalis, pathPrefix));
        }

        /// <summary>The REST handler, to host it elsewhere (ASP.NET Core): it serves request paths under <paramref name="pathPrefix"/>.</summary>
        public static IHttpHandler CreateHandler(ChrysalisServer chrysalis, string pathPrefix = "/api")
        {
            ArgumentNullException.ThrowIfNull(chrysalis);
            return new RestHandler(chrysalis, pathPrefix);
        }
    }

    internal sealed class RestHandler(ChrysalisServer chrysalis, string pathPrefix) : IHttpHandler
    {
        public const string Protocol = "REST";

        private static readonly HashSet<string> BodyMethods = new(StringComparer.OrdinalIgnoreCase) { "POST", "PUT", "PATCH" };

        private readonly string[] prefix = Segments(pathPrefix);
        private readonly Dictionary<ChrysalisOperation, RouteTemplate> templates = [];

        private SerializationProfile Profile => chrysalis.Options.Profile;

        public async ValueTask HandleAsync(HttpServerContext http)
        {
            var request = http.Request;
            var path = Segments(request.RawPath).Skip(prefix.Length).ToArray();

            ChrysalisOperation? operation = null;
            Dictionary<string, string>? routeValues = null;
            var allowed = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var service in chrysalis.Services)
            {
                foreach (var candidate in service.Operations)
                {
                    var template = Template(candidate);
                    if (!template.TryMatch(path, out var values))
                        continue;
                    allowed.Add(template.Method);
                    if (operation is null && (template.Method == request.Method || (template.Method == "GET" && request.Method == "HEAD")))
                        (operation, routeValues) = (candidate, values);
                }
            }

            if (operation is null)
            {
                if (allowed.Count == 0)
                {
                    WriteProblem(http.Response, ChrysalisStatus.NotFound, $"No operation answers {request.Path}.");
                }
                else
                {
                    WriteProblem(http.Response, 405, "Method Not Allowed", $"{request.Path} accepts {string.Join(", ", allowed)}.", null);
                    http.Response.Headers["Allow"] = string.Join(", ", allowed);
                }
                return;
            }

            try
            {
                var arguments = Bind(operation, routeValues!, request);
                var context = ChrysalisHttp.CreateCallContext(operation, arguments, Protocol, http, http.RequestAborted);
                var result = await chrysalis.InvokeAsync(context).ConfigureAwait(false);

                if (operation.ReturnType is null)
                {
                    http.Response.StatusCode = 204;
                    return;
                }
                WriteJson(http.Response, writer => JsonFormat.Instance.Write(writer, operation.ReturnType, result, Profile), "application/json; charset=utf-8");
            }
            catch (ChrysalisException exception)
            {
                http.Response.Reset();
                WriteProblem(http.Response, exception.Status, exception.Message);
            }
            catch (SerializationException exception)
            {
                // The result could not be written (too deep, a list of lists in a dynamic value...).
                http.Response.Reset();
                WriteProblem(http.Response, ChrysalisStatus.Internal, exception.Message);
            }
        }

        private object?[] Bind(ChrysalisOperation operation, Dictionary<string, string> routeValues, HttpServerRequest request)
        {
            var arguments = operation.CreateDefaultArguments();
            var bound = new HashSet<int>();

            foreach (var (name, text) in routeValues)
            {
                var parameter = operation.FindParameter(name, out var index)!;
                arguments[index] = Parse(parameter, text, name);
                bound.Add(index);
            }

            var remaining = Enumerable.Range(0, operation.Parameters.Count).Where(index => !bound.Contains(index)).ToList();
            if (BodyMethods.Contains(request.Method))
                BindBody(operation, request, arguments, remaining);
            else
                BindQuery(operation, request, arguments, remaining);
            return arguments;
        }

        private void BindBody(ChrysalisOperation operation, HttpServerRequest request, object?[] arguments, List<int> remaining)
        {
            if (request.Body.IsEmpty)
            {
                // A single message or list parameter is the body itself, so it cannot be missing.
                if (remaining.Count == 1 && !operation.Parameters[remaining[0]].Type.IsScalar && !operation.Parameters[remaining[0]].IsOptional)
                    throw new ChrysalisException(ChrysalisStatus.InvalidArgument, $"The request body ({operation.Parameters[remaining[0]].Name}) is required.");
                return;
            }

            var mediaType = request.MediaType;
            if (mediaType is not null && mediaType != "application/json" && !mediaType.EndsWith("+json", StringComparison.Ordinal))
                throw new ChrysalisException(ChrysalisStatus.InvalidArgument, "The request body must be JSON (Content-Type: application/json).");

            using var document = Parse(request.Body, Profile.MaxDepth);
            if (remaining.Count == 1 && !operation.Parameters[remaining[0]].Type.IsScalar)
            {
                var parameter = operation.Parameters[remaining[0]];
                try
                {
                    arguments[remaining[0]] = JsonFormat.Instance.Read(document.RootElement, parameter.Type, Profile) ?? parameter.CreateDefault();
                }
                catch (SerializationException exception)
                {
                    throw ChrysalisException.InvalidInput(exception, parameter.Name);
                }
                return;
            }

            // Otherwise the body is an object with one member per parameter; route values win over the body.
            object?[] values;
            try
            {
                values = (object?[])JsonFormat.Instance.Read(document.RootElement, operation.ParametersType, Profile)!;
            }
            catch (SerializationException exception)
            {
                throw ChrysalisException.InvalidInput(exception);
            }
            foreach (var index in remaining)
                arguments[index] = values[index];
        }

        private static void BindQuery(ChrysalisOperation operation, HttpServerRequest request, object?[] arguments, List<int> remaining)
        {
            foreach (var index in remaining)
            {
                var parameter = operation.Parameters[index];
                var values = request.Query[parameter.Name].ToList();
                if (values.Count == 0)
                    continue;

                if (parameter.Type is ListType list && list.Element.IsScalar)
                    arguments[index] = list.Create([.. values.Select((value, i) => Parse(new ChrysalisParameter(parameter.Name, list.Element, false), value, $"{parameter.Name}[{i}]"))]);
                else if (parameter.Type.IsScalar)
                    arguments[index] = Parse(parameter, values[^1], parameter.Name);
                else
                    throw new ChrysalisException(ChrysalisStatus.InvalidArgument, $"'{parameter.Name}' cannot be sent in the query string; use a request body.");
            }
        }

        private static object? Parse(ChrysalisParameter parameter, string text, string name)
        {
            if (!parameter.Type.IsScalar)
                throw new ChrysalisException(ChrysalisStatus.Internal, $"The route parameter '{name}' must be a scalar.");
            return ScalarText.TryParse(parameter.Type, text, out var value)
                ? value
                : throw new ChrysalisException(ChrysalisStatus.InvalidArgument, $"'{name}': '{text}' is not a valid {parameter.Type}.");
        }

        private static JsonDocument Parse(ReadOnlyMemory<byte> body, int maxDepth)
        {
            try
            {
                return JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = maxDepth });
            }
            catch (JsonException exception)
            {
                throw new ChrysalisException(ChrysalisStatus.InvalidArgument, $"The request body is not valid JSON: {exception.Message}");
            }
        }

        private RouteTemplate Template(ChrysalisOperation operation)
        {
            lock (templates)
            {
                if (!templates.TryGetValue(operation, out var template))
                {
                    var route = operation.Http?.Route ?? operation.Name;
                    template = new RouteTemplate(operation.Http?.Method ?? "POST", [.. Segments(operation.Service.Route), .. Segments(route)], operation);
                    templates[operation] = template;
                }
                return template;
            }
        }

        private void WriteProblem(HttpServerResponse response, ChrysalisStatus status, string detail)
        {
            var code = ChrysalisException.ToHttpStatus(status);
            WriteProblem(response, code, HttpReason(code), detail, status.ToString());
        }

        // RFC 9457 problem details; "code" carries the Chrysalis status for programmatic handling.
        private void WriteProblem(HttpServerResponse response, int status, string title, string detail, string? code)
        {
            response.StatusCode = status;
            WriteJson(response, writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("type", "about:blank");
                writer.WriteString("title", title);
                writer.WriteNumber("status", status);
                writer.WriteString("detail", detail);
                if (code is not null)
                    writer.WriteString("code", code);
                writer.WriteEndObject();
            }, "application/problem+json; charset=utf-8");
        }

        private void WriteJson(HttpServerResponse response, Action<Utf8JsonWriter> write, string contentType)
        {
            response.Headers["Content-Type"] = contentType;
            using var writer = new Utf8JsonWriter(response.BodyWriter, JsonFormat.WriterOptions(Profile));
            write(writer);
        }

        private static string HttpReason(int status) => status switch
        {
            400 => "Bad Request",
            401 => "Unauthorized",
            403 => "Forbidden",
            404 => "Not Found",
            409 => "Conflict",
            429 => "Too Many Requests",
            499 => "Client Closed Request",
            501 => "Not Implemented",
            503 => "Service Unavailable",
            504 => "Gateway Timeout",
            _ => "Internal Server Error"
        };

        internal static string[] Segments(string path) => path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        private sealed class RouteTemplate
        {
            private readonly string[] segments;

            public RouteTemplate(string method, string[] segments, ChrysalisOperation operation)
            {
                Method = method.ToUpperInvariant();
                this.segments = segments;
                foreach (var segment in segments.Where(IsParameter))
                {
                    var name = segment[1..^1];
                    if (operation.FindParameter(name, out _) is not { } parameter || !parameter.Type.IsScalar)
                        throw new InvalidOperationException($"The route of {operation} uses {{{name}}}, which is not a scalar parameter of the operation.");
                }
            }

            public string Method { get; }

            public bool TryMatch(string[] path, out Dictionary<string, string> values)
            {
                values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (path.Length != segments.Length)
                    return false;

                for (var i = 0; i < segments.Length; i++)
                {
                    if (IsParameter(segments[i]))
                        values[segments[i][1..^1]] = Uri.UnescapeDataString(path[i]);
                    else if (!segments[i].Equals(Uri.UnescapeDataString(path[i]), StringComparison.OrdinalIgnoreCase))
                        return false;
                }
                return true;
            }

            private static bool IsParameter(string segment) => segment.Length > 2 && segment[0] == '{' && segment[^1] == '}';
        }
    }
}

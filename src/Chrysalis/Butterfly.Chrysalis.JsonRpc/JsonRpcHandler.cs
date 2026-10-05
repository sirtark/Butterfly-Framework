using Butterfly.Chrysalis.Http;
using Butterfly.Serialization;
using Butterfly.Serialization.Json;
using System.Text.Json;

namespace Butterfly.Chrysalis.JsonRpc
{
    public static class JsonRpcHttpServerExtensions
    {
        /// <summary>
        /// Exposes the services of <paramref name="chrysalis"/> as JSON-RPC 2.0 at <paramref name="path"/>: the method of an
        /// operation is "Service.Operation" and its params are positional (array) or named (object).
        /// </summary>
        public static HttpServer MapJsonRpc(this HttpServer http, ChrysalisServer chrysalis, string path = "/rpc", int maxBatchSize = 100)
        {
            ArgumentNullException.ThrowIfNull(http);
            ArgumentNullException.ThrowIfNull(chrysalis);
            return http.Map(path, CreateHandler(chrysalis, maxBatchSize));
        }

        /// <summary>The JSON-RPC handler, to host it elsewhere (ASP.NET Core).</summary>
        public static IHttpHandler CreateHandler(ChrysalisServer chrysalis, int maxBatchSize = 100)
        {
            ArgumentNullException.ThrowIfNull(chrysalis);
            return new JsonRpcHandler(chrysalis, maxBatchSize);
        }
    }

    /// <summary>JSON-RPC 2.0 error codes (https://www.jsonrpc.org/specification#error_object).</summary>
    public static class JsonRpcErrorCodes
    {
        public const int ParseError = -32700;
        public const int InvalidRequest = -32600;
        public const int MethodNotFound = -32601;
        public const int InvalidParams = -32602;
        public const int InternalError = -32603;

        /// <summary>
        /// The code of a Chrysalis status: the standard codes where they apply, otherwise -32000 minus the status
        /// (NotFound: -32005, PermissionDenied: -32007...), inside the range the specification leaves to servers.
        /// </summary>
        public static int FromStatus(ChrysalisStatus status) => status switch
        {
            ChrysalisStatus.InvalidArgument => InvalidParams,
            ChrysalisStatus.Unimplemented => MethodNotFound,
            ChrysalisStatus.Internal or ChrysalisStatus.Unknown or ChrysalisStatus.DataLoss => InternalError,
            _ => -32000 - (int)status
        };
    }

    internal sealed class JsonRpcHandler(ChrysalisServer chrysalis, int maxBatchSize) : IHttpHandler
    {
        public const string Protocol = "JSON-RPC";

        public async ValueTask HandleAsync(HttpServerContext http)
        {
            if (http.Request.Method != "POST")
            {
                http.Response.StatusCode = 405;
                http.Response.Headers["Allow"] = "POST";
                return;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(http.Request.Body, new JsonDocumentOptions { MaxDepth = Profile.MaxDepth });
            }
            catch (JsonException)
            {
                Write(http.Response, writer => WriteError(writer, default, JsonRpcErrorCodes.ParseError, "Parse error", null));
                return;
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Array)
                {
                    var single = await ExecuteAsync(root, http).ConfigureAwait(false);
                    if (single is null)
                        http.Response.StatusCode = 204;
                    else
                        Write(http.Response, single);
                    return;
                }

                if (root.GetArrayLength() == 0 || root.GetArrayLength() > maxBatchSize)
                {
                    Write(http.Response, writer => WriteError(writer, default, JsonRpcErrorCodes.InvalidRequest,
                        root.GetArrayLength() == 0 ? "Invalid Request: empty batch" : $"Invalid Request: batches are limited to {maxBatchSize} calls", null));
                    return;
                }

                // Calls of a batch run concurrently; responses keep the order of the requests and skip notifications.
                var responses = await Task.WhenAll(root.EnumerateArray().Select(element => ExecuteAsync(element, http))).ConfigureAwait(false);
                var written = responses.Where(response => response is not null).ToList();
                if (written.Count == 0)
                {
                    http.Response.StatusCode = 204;
                    return;
                }
                Write(http.Response, writer =>
                {
                    writer.WriteStartArray();
                    foreach (var response in written)
                        response!(writer);
                    writer.WriteEndArray();
                });
            }
        }

        // Runs one request; returns how to write its response, or null for a notification.
        private async Task<Action<Utf8JsonWriter>?> ExecuteAsync(JsonElement request, HttpServerContext http)
        {
            if (request.ValueKind != JsonValueKind.Object)
                return writer => WriteError(writer, default, JsonRpcErrorCodes.InvalidRequest, "Invalid Request", null);

            var hasId = request.TryGetProperty("id", out var id);
            if (hasId && id.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.Null))
                return writer => WriteError(writer, default, JsonRpcErrorCodes.InvalidRequest, "Invalid Request: id must be a string, a number or null", null);
            var requestId = hasId ? id.Clone() : default;

            if (!request.TryGetProperty("jsonrpc", out var version) || version.ValueKind != JsonValueKind.String || version.GetString() != "2.0"
                || !request.TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String)
                return writer => WriteError(writer, requestId, JsonRpcErrorCodes.InvalidRequest, "Invalid Request", null);

            var hasParams = request.TryGetProperty("params", out var parameters);
            if (hasParams && parameters.ValueKind is not (JsonValueKind.Array or JsonValueKind.Object))
                return writer => WriteError(writer, requestId, JsonRpcErrorCodes.InvalidRequest, "Invalid Request: params must be an array or an object", null);

            var operation = chrysalis.FindOperation(method.GetString()!);
            Action<Utf8JsonWriter> response;
            if (operation is null)
            {
                response = writer => WriteError(writer, requestId, JsonRpcErrorCodes.MethodNotFound, "Method not found", null);
            }
            else
            {
                try
                {
                    var arguments = hasParams ? Bind(operation, parameters) : operation.CreateDefaultArguments();
                    var context = ChrysalisHttp.CreateCallContext(operation, arguments, Protocol, http, http.RequestAborted);
                    var result = await chrysalis.InvokeAsync(context).ConfigureAwait(false);
                    // Converted now, so a result that cannot be serialized becomes an error response, not a broken one.
                    var value = operation.ReturnType is null ? SerializationValue.Null : ValueConverter.ToValue(operation.ReturnType, result, JsonFormat.Instance.Conventions, Profile);
                    response = writer =>
                    {
                        writer.WriteStartObject();
                        writer.WriteString("jsonrpc", "2.0");
                        writer.WritePropertyName("result");
                        JsonFormat.Write(writer, value);
                        WriteId(writer, requestId);
                        writer.WriteEndObject();
                    };
                }
                catch (ChrysalisException exception)
                {
                    response = writer => WriteError(writer, requestId, JsonRpcErrorCodes.FromStatus(exception.Status), exception.Message, exception.Status);
                }
                catch (SerializationException exception)
                {
                    response = writer => WriteError(writer, requestId, JsonRpcErrorCodes.InternalError, exception.Message, ChrysalisStatus.Internal);
                }
            }

            // A notification gets no response, not even an error (JSON-RPC 2.0, section 4.1).
            return hasId ? response : null;
        }

        private SerializationProfile Profile => chrysalis.Options.Profile;

        private object?[] Bind(ChrysalisOperation operation, JsonElement parameters)
        {
            if (parameters.ValueKind == JsonValueKind.Object)
            {
                try
                {
                    return (object?[])JsonFormat.Instance.Read(parameters, operation.ParametersType, Profile)!;
                }
                catch (SerializationException exception)
                {
                    throw ChrysalisException.InvalidInput(exception);
                }
            }

            if (parameters.GetArrayLength() > operation.Parameters.Count)
                throw new ChrysalisException(ChrysalisStatus.InvalidArgument, $"{operation.FullName} takes {operation.Parameters.Count} parameters.");

            var arguments = operation.CreateDefaultArguments();
            var index = 0;
            foreach (var value in parameters.EnumerateArray())
            {
                var parameter = operation.Parameters[index];
                try
                {
                    arguments[index++] = JsonFormat.Instance.Read(value, parameter.Type, Profile) ?? parameter.CreateDefault();
                }
                catch (SerializationException exception)
                {
                    throw ChrysalisException.InvalidInput(exception, parameter.Name);
                }
            }
            return arguments;
        }

        private static void WriteError(Utf8JsonWriter writer, JsonElement id, int code, string message, ChrysalisStatus? status)
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WriteStartObject("error");
            writer.WriteNumber("code", code);
            writer.WriteString("message", message);
            if (status is not null)
            {
                writer.WriteStartObject("data");
                writer.WriteString("status", status.Value.ToString());
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
            WriteId(writer, id);
            writer.WriteEndObject();
        }

        // The id is echoed as received; when it could not be read the response carries null.
        private static void WriteId(Utf8JsonWriter writer, JsonElement id)
        {
            writer.WritePropertyName("id");
            if (id.ValueKind == JsonValueKind.Undefined)
                writer.WriteNullValue();
            else
                id.WriteTo(writer);
        }

        private static void Write(HttpServerResponse response, Action<Utf8JsonWriter> write)
        {
            response.Headers["Content-Type"] = "application/json; charset=utf-8";
            using var writer = new Utf8JsonWriter(response.BodyWriter, JsonFormat.WriterOptions(SerializationProfile.Default));
            write(writer);
        }
    }
}

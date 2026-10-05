using Butterfly.Chrysalis.Http;
using Butterfly.Serialization;
using Butterfly.Serialization.Protobuf;
using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Butterfly.Chrysalis.Grpc
{
    public static class GrpcHttpServerExtensions
    {
        /// <summary>
        /// Exposes the services of <paramref name="chrysalis"/> as gRPC: every HTTP/2 request with a gRPC content type is
        /// served as "/{package}.{Service}/{Operation}", whatever other paths are mapped.
        /// </summary>
        public static HttpServer MapGrpc(this HttpServer http, ChrysalisServer chrysalis)
        {
            ArgumentNullException.ThrowIfNull(http);
            ArgumentNullException.ThrowIfNull(chrysalis);
            return http.MapWhen(IsGrpcRequest, CreateHandler(chrysalis));
        }

        /// <summary>The gRPC handler, to host it elsewhere (ASP.NET Core); it serves the requests <see cref="IsGrpcRequest"/> accepts.</summary>
        public static IHttpHandler CreateHandler(ChrysalisServer chrysalis)
        {
            ArgumentNullException.ThrowIfNull(chrysalis);
            return new GrpcHandler(chrysalis);
        }

        /// <summary>Whether a request is a gRPC call (by its content type).</summary>
        public static bool IsGrpcRequest(HttpServerRequest request) => GrpcHandler.IsGrpc(request);

        /// <summary>Whether a content type is the gRPC one (application/grpc, application/grpc+proto...).</summary>
        public static bool IsGrpcContentType(string? contentType) =>
            contentType?.Split(';')[0].Trim().ToLowerInvariant() is { } type && (type == "application/grpc" || type.StartsWith("application/grpc+", StringComparison.Ordinal));
    }

    /// <summary>gRPC over HTTP/2 (https://github.com/grpc/grpc/blob/master/doc/PROTOCOL-HTTP2.md), unary calls only.</summary>
    internal sealed class GrpcHandler(ChrysalisServer chrysalis) : IHttpHandler
    {
        public const string Protocol = "gRPC";

        public static bool IsGrpc(HttpServerRequest request) =>
            request.MediaType is { } type && (type == "application/grpc" || type.StartsWith("application/grpc+", StringComparison.Ordinal));

        public async ValueTask HandleAsync(HttpServerContext http)
        {
            var request = http.Request;
            var response = http.Response;

            if (request.Protocol != "HTTP/2")
            {
                response.StatusCode = 505;
                response.Write("gRPC requires HTTP/2.", "text/plain; charset=utf-8");
                return;
            }
            if (request.Method != "POST")
            {
                response.StatusCode = 405;
                response.Headers["Allow"] = "POST";
                return;
            }
            if (request.MediaType is not ("application/grpc" or "application/grpc+proto"))
            {
                response.StatusCode = 415;
                return;
            }

            response.Headers["Content-Type"] = "application/grpc";
            try
            {
                var operation = FindOperation(request.Path);
                var payload = ReadSingleMessage(request);
                object message;
                try
                {
                    message = ProtobufFormat.Instance.Deserialize(GrpcMessages.RequestType(operation), payload, chrysalis.Options.Profile)!;
                }
                catch (SerializationException exception)
                {
                    throw ChrysalisException.InvalidInput(exception);
                }
                var arguments = GrpcMessages.ToArguments(operation, message);

                using var deadline = Deadline(request.Headers["grpc-timeout"], http.RequestAborted);
                var context = ChrysalisHttp.CreateCallContext(operation, arguments, Protocol, http, deadline.Token);
                object? result;
                try
                {
                    result = await chrysalis.InvokeAsync(context).ConfigureAwait(false);
                }
                catch (ChrysalisException exception) when (exception.Status == ChrysalisStatus.Cancelled && deadline.IsCancellationRequested && !http.RequestAborted.IsCancellationRequested)
                {
                    throw new ChrysalisException(ChrysalisStatus.DeadlineExceeded, "The deadline of the call expired.");
                }

                byte[] bytes;
                try
                {
                    bytes = ProtobufFormat.Instance.Serialize(GrpcMessages.ResponseType(operation), GrpcMessages.ToResponse(operation, result), chrysalis.Options.Profile);
                }
                catch (SerializationException exception)
                {
                    throw new ChrysalisException(ChrysalisStatus.Internal, exception.Message, exception);
                }
                var frame = new byte[5 + bytes.Length];
                BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(1), (uint)bytes.Length);
                bytes.CopyTo(frame, 5);
                response.Write(frame);
                response.Trailers.Add("grpc-status", "0");
            }
            catch (ChrysalisException exception)
            {
                // Trailers-only response: the status travels in the only HEADERS frame (with END_STREAM).
                response.Reset();
                response.Headers["Content-Type"] = "application/grpc";
                response.Headers["grpc-status"] = ((int)exception.Status).ToString(CultureInfo.InvariantCulture);
                response.Headers["grpc-message"] = PercentEncode(exception.Message);
                if (exception.Status == ChrysalisStatus.Unimplemented && request.Headers["grpc-encoding"] is not null)
                    response.Headers["grpc-accept-encoding"] = "identity, gzip";
            }
        }

        private ChrysalisOperation FindOperation(string path)
        {
            // "/package.Service/Method"
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2)
            {
                var service = chrysalis.Services.FirstOrDefault(service => GrpcMessages.ServiceName(service) == parts[0]);
                if (service?.FindOperation(parts[1]) is { } operation)
                    return operation;
            }
            throw new ChrysalisException(ChrysalisStatus.Unimplemented, $"Method {path} is not implemented.");
        }

        // A unary request carries exactly one length-prefixed message.
        private static byte[] ReadSingleMessage(HttpServerRequest request)
        {
            var body = request.Body.Span;
            if (body.Length < 5)
                throw new ChrysalisException(ChrysalisStatus.Internal, "The request has no message.");

            var compressed = body[0];
            var length = BinaryPrimitives.ReadUInt32BigEndian(body[1..]);
            if (length != body.Length - 5)
                throw new ChrysalisException(body.Length - 5 > length ? ChrysalisStatus.Unimplemented : ChrysalisStatus.Internal,
                    body.Length - 5 > length ? "Streaming requests are not supported." : "The request message is truncated.");

            var message = body[5..].ToArray();
            if (compressed == 0)
                return message;
            if (compressed != 1)
                throw new ChrysalisException(ChrysalisStatus.Internal, "Invalid compressed flag.");

            var encoding = request.Headers["grpc-encoding"] ?? "identity";
            if (encoding != "gzip")
                throw new ChrysalisException(ChrysalisStatus.Unimplemented, $"The message encoding '{encoding}' is not supported.");
            try
            {
                using var gzip = new GZipStream(new MemoryStream(message), CompressionMode.Decompress);
                using var output = new MemoryStream();
                // Bounded copy: a small compressed message must not expand without limit (zip bomb).
                var buffer = new byte[81920];
                int read;
                while ((read = gzip.Read(buffer)) > 0)
                {
                    if (output.Length + read > 64L * 1024 * 1024)
                        throw new ChrysalisException(ChrysalisStatus.ResourceExhausted, "The decompressed message is too large.");
                    output.Write(buffer, 0, read);
                }
                return output.ToArray();
            }
            catch (InvalidDataException)
            {
                throw new ChrysalisException(ChrysalisStatus.Internal, "The compressed message is corrupt.");
            }
        }

        // grpc-timeout: up to 8 digits and a unit (H, M, S, m, u, n).
        internal static CancellationTokenSource Deadline(string? timeout, CancellationToken aborted)
        {
            var source = CancellationTokenSource.CreateLinkedTokenSource(aborted);
            if (timeout is { Length: >= 2 and <= 9 } && long.TryParse(timeout.AsSpan(0, timeout.Length - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var amount))
            {
                TimeSpan? span = timeout[^1] switch
                {
                    'H' => TimeSpan.FromHours(amount),
                    'M' => TimeSpan.FromMinutes(amount),
                    'S' => TimeSpan.FromSeconds(amount),
                    'm' => TimeSpan.FromMilliseconds(amount),
                    'u' => TimeSpan.FromTicks(amount * 10),
                    'n' => TimeSpan.FromTicks(amount / 100),
                    _ => null
                };
                if (span is { } value)
                    source.CancelAfter(value < TimeSpan.FromDays(24) ? value : TimeSpan.FromDays(24));
            }
            return source;
        }

        // grpc-message is percent-encoded UTF-8: printable ASCII except '%' stays as is.
        internal static string PercentEncode(string message)
        {
            var builder = new StringBuilder(message.Length);
            foreach (var b in Encoding.UTF8.GetBytes(message))
            {
                if (b is >= 0x20 and <= 0x7E && b != (byte)'%')
                    builder.Append((char)b);
                else
                    builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
            return builder.ToString();
        }
    }
}

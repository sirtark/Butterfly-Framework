using Butterfly.Serialization;
using System.Security.Claims;

namespace Butterfly.Chrysalis
{
    /// <summary>One call to an operation, as it flows through the middleware pipeline.</summary>
    public sealed class ChrysalisCallContext
    {
        public ChrysalisCallContext(ChrysalisOperation operation, object?[] arguments, string protocol, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(operation);
            ArgumentNullException.ThrowIfNull(arguments);
            if (arguments.Length != operation.Parameters.Count)
                throw new ArgumentException($"{operation} expects {operation.Parameters.Count} arguments, not {arguments.Length}.", nameof(arguments));

            Operation = operation;
            Arguments = arguments;
            Protocol = protocol;
            CancellationToken = cancellationToken;
        }

        public ChrysalisService Service => Operation.Service;
        public ChrysalisOperation Operation { get; }
        /// <summary>One value per parameter. Middleware may replace values before the operation runs.</summary>
        public object?[] Arguments { get; }
        /// <summary>"REST", "SOAP", "JSON-RPC", "XML-RPC", "gRPC", "Binary"...</summary>
        public string Protocol { get; }
        /// <summary>Transport metadata: HTTP headers or gRPC metadata. Names are case-insensitive.</summary>
        public IDictionary<string, string> Headers { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string? RemoteAddress { get; init; }
        /// <summary>Set by authentication middleware.</summary>
        public ClaimsPrincipal? User { get; set; }
        /// <summary>Free storage for middleware.</summary>
        public IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        /// <summary>Signalled when the caller goes away or its deadline passes.</summary>
        public CancellationToken CancellationToken { get; }
    }

    public delegate ValueTask<object?> ChrysalisNext(ChrysalisCallContext context);

    /// <summary>
    /// Runs around every call, whatever the protocol: authentication, logging, validation, metrics... It returns the
    /// result of the operation (or of <paramref name="next"/>), and may throw <see cref="ChrysalisException"/> to fail the call.
    /// </summary>
    public interface IChrysalisMiddleware
    {
        public ValueTask<object?> InvokeAsync(ChrysalisCallContext context, ChrysalisNext next);
    }

    /// <summary>Sends calls somewhere: what the generated typed clients (<c>{Service}Client</c>) are built on.</summary>
    public interface IChrysalisInvoker
    {
        public ValueTask<object?> InvokeAsync(ChrysalisOperation operation, object?[] arguments, CancellationToken cancellationToken);
    }
}

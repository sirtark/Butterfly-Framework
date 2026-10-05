using Butterfly.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Butterfly.Chrysalis
{
    public sealed class ChrysalisServerOptions
    {
        /// <summary>
        /// Sends the message of unexpected exceptions to callers. Off by default: it can reveal implementation details,
        /// so callers only see "An internal error occurred.".
        /// </summary>
        public bool IncludeExceptionDetails { get; set; }
        public ILogger Logger { get; set; } = NullLogger.Instance;
        /// <summary>
        /// How every protocol serializes: which members are visible (and accepted in requests), naming and enum format.
        /// Protocols keep their usual conventions where the profile says Default (camelCase JSON, declared names in XML).
        /// </summary>
        public SerializationProfile Profile { get; set; } = SerializationProfile.Default;
    }

    /// <summary>
    /// The core of a Chrysalis server: the services it exposes and the pipeline every call goes through. It knows
    /// nothing about protocols; hosts (HTTP for REST/SOAP/JSON-RPC/XML-RPC/gRPC, TCP for binary RPC) decode a request,
    /// call <see cref="InvokeAsync"/> and encode the result.
    /// </summary>
    public sealed class ChrysalisServer
    {
        private const string InternalErrorMessage = "An internal error occurred.";

        private readonly Lock gate = new();
        private Dictionary<string, (ChrysalisService Service, Func<ChrysalisCallContext, object> Resolve)> services = new(StringComparer.Ordinal);
        private IChrysalisMiddleware[] middleware = [];

        public ChrysalisServer(ChrysalisServerOptions? options = null)
        {
            Options = options ?? new ChrysalisServerOptions();
        }

        public ChrysalisServerOptions Options { get; }

        public IReadOnlyList<ChrysalisService> Services => [.. services.Values.Select(entry => entry.Service)];

        /// <summary>Exposes a service with a single implementation shared by every call (it must be thread-safe).</summary>
        public ChrysalisServer Expose<TContract>(TContract implementation) where TContract : class
        {
            ArgumentNullException.ThrowIfNull(implementation);
            return Expose<TContract>(_ => implementation);
        }

        /// <summary>Exposes a service whose implementation is resolved for every call (per-call instances, dependency injection...).</summary>
        public ChrysalisServer Expose<TContract>(Func<ChrysalisCallContext, TContract> resolve) where TContract : class
        {
            ArgumentNullException.ThrowIfNull(resolve);
            var service = ChrysalisRegistry.GetService<TContract>();
            lock (gate)
            {
                if (services.ContainsKey(service.Name))
                    throw new InvalidOperationException($"A service named '{service.Name}' is already exposed.");
                services = new(services, StringComparer.Ordinal) { [service.Name] = (service, resolve) };
            }
            return this;
        }

        public ChrysalisServer Use(IChrysalisMiddleware middleware)
        {
            ArgumentNullException.ThrowIfNull(middleware);
            lock (gate)
                this.middleware = [.. this.middleware, middleware];
            return this;
        }

        public ChrysalisServer Use(Func<ChrysalisCallContext, ChrysalisNext, ValueTask<object?>> middleware) => Use(new DelegateMiddleware(middleware));

        public ChrysalisService? FindService(string name) => services.TryGetValue(name, out var entry) ? entry.Service : null;

        public ChrysalisOperation? FindOperation(string service, string operation) => FindService(service)?.FindOperation(operation);

        /// <summary>Finds an operation by its full name, "Service.Operation".</summary>
        public ChrysalisOperation? FindOperation(string fullName)
        {
            var dot = fullName.LastIndexOf('.');
            return dot <= 0 ? null : FindOperation(fullName[..dot], fullName[(dot + 1)..]);
        }

        /// <summary>Runs a call through the middleware and the implementation.</summary>
        /// <exception cref="ChrysalisException">Always the type of the exception: anything else thrown is translated (see <see cref="ToChrysalisException"/>).</exception>
        public async ValueTask<object?> InvokeAsync(ChrysalisCallContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            if (!services.TryGetValue(context.Service.Name, out var entry) || !ReferenceEquals(entry.Service, context.Service))
                throw new ChrysalisException(ChrysalisStatus.Unimplemented, $"The service '{context.Service.Name}' is not exposed.");

            var pipeline = middleware;
            var index = 0;
            ValueTask<object?> Next(ChrysalisCallContext current) =>
                index < pipeline.Length ? pipeline[index++].InvokeAsync(current, Next) : InvokeOperationAsync(current);

            // Exceptions are translated where the operation runs, so middleware always sees a ChrysalisException with its status.
            async ValueTask<object?> InvokeOperationAsync(ChrysalisCallContext current)
            {
                try
                {
                    current.CancellationToken.ThrowIfCancellationRequested();
                    return await current.Operation.InvokeAsync(entry.Resolve(current), current.Arguments, current).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not ChrysalisException)
                {
                    throw ToChrysalisException(exception, current);
                }
            }

            try
            {
                return await Next(context).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                throw ToChrysalisException(exception, context);
            }
        }

        /// <summary>
        /// Translates an exception into what callers see. Common .NET exceptions map to their natural status
        /// (ArgumentException: InvalidArgument, KeyNotFoundException: NotFound...); the rest are internal errors.
        /// </summary>
        public ChrysalisException ToChrysalisException(Exception exception, ChrysalisCallContext? context = null)
        {
            if (exception is ChrysalisException chrysalis)
                return chrysalis;

            var status = exception switch
            {
                OperationCanceledException => ChrysalisStatus.Cancelled,
                SerializationException or ArgumentException or FormatException => ChrysalisStatus.InvalidArgument,
                KeyNotFoundException or FileNotFoundException => ChrysalisStatus.NotFound,
                UnauthorizedAccessException => ChrysalisStatus.PermissionDenied,
                NotImplementedException or NotSupportedException => ChrysalisStatus.Unimplemented,
                TimeoutException => ChrysalisStatus.DeadlineExceeded,
                InvalidOperationException => ChrysalisStatus.FailedPrecondition,
                _ => ChrysalisStatus.Internal
            };

            if (status == ChrysalisStatus.Internal)
            {
                Options.Logger.LogError(exception, "Chrysalis call {Operation} failed.", context?.Operation.ToString() ?? "(unknown)");
                return new ChrysalisException(status, Options.IncludeExceptionDetails ? exception.Message : InternalErrorMessage, exception);
            }
            return new ChrysalisException(status, exception.Message, exception);
        }

        private sealed class DelegateMiddleware(Func<ChrysalisCallContext, ChrysalisNext, ValueTask<object?>> invoke) : IChrysalisMiddleware
        {
            public ValueTask<object?> InvokeAsync(ChrysalisCallContext context, ChrysalisNext next) => invoke(context, next);
        }
    }
}

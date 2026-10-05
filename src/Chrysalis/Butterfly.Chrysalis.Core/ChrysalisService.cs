using Butterfly.Serialization;
namespace Butterfly.Chrysalis
{
    /// <summary>Calls the implementation of an operation: <paramref name="service"/> is the object that implements the contract.</summary>
    public delegate ValueTask<object?> ChrysalisInvoke(object service, object?[] arguments, ChrysalisCallContext context);

    public sealed record ChrysalisParameter(string Name, SerializableType Type, bool IsOptional)
    {
        public object? CreateDefault() => IsOptional ? null : Type.CreateDefault();
    }

    /// <summary>REST binding declared with <see cref="HttpMethodAttribute"/>.</summary>
    public sealed record ChrysalisHttpBinding(string Method, string? Route);

    public sealed class ChrysalisOperation
    {
        private readonly ChrysalisInvoke invoke;
        private ChrysalisService? service;

        /// <param name="returnType">Null for operations that return nothing.</param>
        public ChrysalisOperation(string name, IReadOnlyList<ChrysalisParameter> parameters, SerializableType? returnType, bool returnIsOptional, ChrysalisInvoke invoke, ChrysalisHttpBinding? http = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            Name = name;
            Parameters = parameters;
            ReturnType = returnType;
            ReturnIsOptional = returnIsOptional;
            Http = http;
            this.invoke = invoke;
        }

        public ChrysalisService Service => service ?? throw new InvalidOperationException($"The operation '{Name}' does not belong to a service yet.");
        public string Name { get; }
        /// <summary>"Service.Operation": the method name of JSON-RPC and XML-RPC, and of the binary protocol.</summary>
        public string FullName => Service.Name + "." + Name;
        public IReadOnlyList<ChrysalisParameter> Parameters { get; }
        public SerializableType? ReturnType { get; }
        public bool ReturnIsOptional { get; }
        public ChrysalisHttpBinding? Http { get; }

        /// <summary>The parameters as a message ("{Operation}Request", fields numbered from 1): how binary encodings carry the arguments.</summary>
        public ObjectType ParametersType { get; private set; } = null!;
        /// <summary>The result as a message ("{Operation}Response" with a "result" field = 1, or no fields when nothing is returned).</summary>
        public ObjectType ResultType { get; private set; } = null!;

        public object?[] CreateDefaultArguments()
        {
            var arguments = new object?[Parameters.Count];
            for (var i = 0; i < arguments.Length; i++)
                arguments[i] = Parameters[i].CreateDefault();
            return arguments;
        }

        public ChrysalisParameter? FindParameter(string name, out int index)
        {
            for (index = 0; index < Parameters.Count; index++)
            {
                if (string.Equals(Parameters[index].Name, name, StringComparison.OrdinalIgnoreCase))
                    return Parameters[index];
            }
            index = -1;
            return null;
        }

        internal ValueTask<object?> InvokeAsync(object implementation, object?[] arguments, ChrysalisCallContext context) => invoke(implementation, arguments, context);

        internal void Attach(ChrysalisService owner)
        {
            if (service is not null)
                throw new InvalidOperationException($"The operation '{Name}' already belongs to the service '{service.Name}'.");
            service = owner;

            ParametersType = ObjectType.CreateArrayBacked(Name + "Request", owner.Namespace,
                Parameters.Select((parameter, index) => (parameter.Name, index + 1, parameter.Type, parameter.IsOptional)));
            ResultType = ObjectType.CreateArrayBacked(Name + "Response", owner.Namespace,
                ReturnType is null ? [] : [("result", 1, ReturnType, ReturnIsOptional)]);
        }

        public override string ToString() => service is null ? Name : FullName;
    }

    public sealed class ChrysalisService
    {
        private readonly Dictionary<string, ChrysalisOperation> operationsByName;

        public ChrysalisService(string name, string @namespace, Type contractType, string? route, IReadOnlyList<ChrysalisOperation> operations)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            Name = name;
            Namespace = @namespace;
            ContractType = contractType;
            Route = string.IsNullOrWhiteSpace(route) ? name : route.Trim('/');
            Operations = operations;

            operationsByName = new Dictionary<string, ChrysalisOperation>(StringComparer.Ordinal);
            foreach (var operation in operations)
            {
                if (!operationsByName.TryAdd(operation.Name, operation))
                    throw new ArgumentException($"The service '{name}' has two operations named '{operation.Name}'. Rename one with [ChrysalisName].", nameof(operations));
                operation.Attach(this);
            }
        }

        public string Name { get; }
        /// <summary>gRPC package and the base of the SOAP target namespace.</summary>
        public string Namespace { get; }
        public Type ContractType { get; }
        public string Route { get; }
        public IReadOnlyList<ChrysalisOperation> Operations { get; }

        public ChrysalisOperation? FindOperation(string name) => operationsByName.GetValueOrDefault(name);

        public override string ToString() => Name;
    }
}

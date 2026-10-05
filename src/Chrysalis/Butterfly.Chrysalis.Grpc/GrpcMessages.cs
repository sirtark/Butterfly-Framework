using Butterfly.Serialization;
namespace Butterfly.Chrysalis.Grpc
{
    /// <summary>
    /// How an operation looks in gRPC, following the usual .proto style: a single message parameter is the request
    /// itself ("rpc AddProduct(Product)"); otherwise the parameters form "{Operation}Request". A message result is the
    /// response itself; anything else is wrapped in "{Operation}Response" (empty when nothing is returned).
    /// </summary>
    public static class GrpcMessages
    {
        public static ObjectType RequestType(ChrysalisOperation operation) =>
            IsBareRequest(operation) ? (ObjectType)operation.Parameters[0].Type : operation.ParametersType;

        public static ObjectType ResponseType(ChrysalisOperation operation) =>
            operation.ReturnType as ObjectType ?? operation.ResultType;

        public static bool IsBareRequest(ChrysalisOperation operation) =>
            operation.Parameters.Count == 1 && operation.Parameters[0].Type is ObjectType;

        /// <summary>The arguments of a call from its decoded request message.</summary>
        public static object?[] ToArguments(ChrysalisOperation operation, object request) =>
            IsBareRequest(operation) ? [request] : (object?[])request;

        /// <summary>The response message of a result (an empty message stands for a null result).</summary>
        public static object ToResponse(ChrysalisOperation operation, object? result)
        {
            if (operation.ReturnType is ObjectType message)
                return result ?? message.Create(message.CreateDefaultValues());
            return operation.ReturnType is null ? Array.Empty<object?>() : new[] { result };
        }

        /// <summary>The full gRPC service name: "package.Service".</summary>
        public static string ServiceName(ChrysalisService service) =>
            string.IsNullOrEmpty(service.Namespace) ? service.Name : service.Namespace + "." + service.Name;
    }
}

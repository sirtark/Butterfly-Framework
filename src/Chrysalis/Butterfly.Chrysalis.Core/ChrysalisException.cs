namespace Butterfly.Chrysalis
{
    /// <summary>
    /// Outcome of a call, shared by every protocol (the gRPC status codes, which cover the usual cases). Each protocol
    /// translates it: an HTTP status for REST, a fault for SOAP and XML-RPC, an error code for JSON-RPC.
    /// </summary>
    public enum ChrysalisStatus
    {
        Ok = 0,
        Cancelled = 1,
        Unknown = 2,
        InvalidArgument = 3,
        DeadlineExceeded = 4,
        NotFound = 5,
        AlreadyExists = 6,
        PermissionDenied = 7,
        ResourceExhausted = 8,
        FailedPrecondition = 9,
        Aborted = 10,
        OutOfRange = 11,
        Unimplemented = 12,
        Internal = 13,
        Unavailable = 14,
        DataLoss = 15,
        Unauthenticated = 16
    }

    /// <summary>An error a service reports to its callers. Any other exception reaches them as <see cref="ChrysalisStatus.Internal"/>.</summary>
    public class ChrysalisException : Exception
    {
        public ChrysalisException(ChrysalisStatus status, string message) : base(message)
        {
            Status = status;
        }
        public ChrysalisException(ChrysalisStatus status, string message, Exception? innerException) : base(message, innerException)
        {
            Status = status;
        }

        public ChrysalisStatus Status { get; }

        /// <summary>
        /// A request that does not match the contract: InvalidArgument naming the bad value, with <paramref name="prefix"/>
        /// (usually the parameter) in front of its path.
        /// </summary>
        public static ChrysalisException InvalidInput(Butterfly.Serialization.SerializationException exception, string? prefix = null)
        {
            var path = prefix is null || prefix.Length == 0 ? exception.Path
                : exception.Path.Length == 0 ? prefix
                : exception.Path.StartsWith('[') ? prefix + exception.Path
                : prefix + "." + exception.Path;
            return new(ChrysalisStatus.InvalidArgument, path.Length == 0 ? exception.Problem : $"'{path}': {exception.Problem}", exception);
        }

        /// <summary>Whether the caller is to blame (bad input, missing permission) rather than the server.</summary>
        public bool IsClientError => IsClientStatus(Status);

        public static bool IsClientStatus(ChrysalisStatus status) => status is ChrysalisStatus.InvalidArgument or ChrysalisStatus.NotFound
            or ChrysalisStatus.AlreadyExists or ChrysalisStatus.PermissionDenied or ChrysalisStatus.FailedPrecondition
            or ChrysalisStatus.OutOfRange or ChrysalisStatus.Unauthenticated or ChrysalisStatus.Cancelled or ChrysalisStatus.Unimplemented;

        /// <summary>HTTP status code that matches a Chrysalis status (as gRPC-HTTP transcoding maps them).</summary>
        public static int ToHttpStatus(ChrysalisStatus status) => status switch
        {
            ChrysalisStatus.Ok => 200,
            ChrysalisStatus.Cancelled => 499,
            ChrysalisStatus.InvalidArgument or ChrysalisStatus.FailedPrecondition or ChrysalisStatus.OutOfRange => 400,
            ChrysalisStatus.Unauthenticated => 401,
            ChrysalisStatus.PermissionDenied => 403,
            ChrysalisStatus.NotFound => 404,
            ChrysalisStatus.AlreadyExists or ChrysalisStatus.Aborted => 409,
            ChrysalisStatus.ResourceExhausted => 429,
            ChrysalisStatus.Unimplemented => 501,
            ChrysalisStatus.Unavailable => 503,
            ChrysalisStatus.DeadlineExceeded => 504,
            _ => 500
        };
    }
}

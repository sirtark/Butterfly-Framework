namespace Butterfly.Networking.Sockets
{
    public class SocketException(SocketError error, int nativeErrorCode, string? operation = null)
        : Exception(BuildMessage(error, nativeErrorCode, operation))
    {
        public SocketError Error { get; } = error;
        public int NativeErrorCode { get; } = nativeErrorCode;
        public string? Operation { get; } = operation;

        private static string BuildMessage(SocketError error, int nativeErrorCode, string? operation)
            => operation is null
                ? $"Socket error {error} (native error {nativeErrorCode})."
                : $"'{operation}' failed with {error} (native error {nativeErrorCode}).";
    }
}

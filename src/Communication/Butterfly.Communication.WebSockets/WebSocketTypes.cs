using System.Text;

using Butterfly.Communication.Http;

namespace Butterfly.Communication.WebSockets
{
    public enum WebSocketMessageType
    {
        Text,
        Binary
    }

    public enum WebSocketState
    {
        Open,

        /// <summary>We sent a close frame and wait for the server's.</summary>
        CloseSent,

        Closed
    }

    /// <summary>Close codes of RFC 6455 section 7.4.1. Applications may also use 3000-4999.</summary>
    public enum WebSocketCloseStatus : ushort
    {
        Normal = 1000,
        GoingAway = 1001,
        ProtocolError = 1002,
        UnsupportedData = 1003,

        /// <summary>The close frame had no status code (never sent on the wire).</summary>
        NoStatus = 1005,

        /// <summary>The connection dropped without a close frame (never sent on the wire).</summary>
        Abnormal = 1006,

        InvalidPayload = 1007,
        PolicyViolation = 1008,
        MessageTooBig = 1009,
        MandatoryExtension = 1010,
        InternalError = 1011
    }

    public sealed class WebSocketMessage(WebSocketMessageType type, byte[] data)
    {
        public WebSocketMessageType Type { get; } = type;
        public byte[] Data { get; } = data;

        /// <summary>The payload as text (text messages are validated UTF-8).</summary>
        public string Text => Encoding.UTF8.GetString(Data);

        public override string ToString() => Type == WebSocketMessageType.Text ? Text : $"<{Data.Length} bytes>";
    }

    public sealed class WebSocketClientOptions
    {
        public ConnectionOptions Connection { get; init; } = ConnectionOptions.Default;

        /// <summary>Sub-protocols offered in Sec-WebSocket-Protocol, in order of preference.</summary>
        public IReadOnlyList<string> SubProtocols { get; init; } = [];

        /// <summary>Extra handshake headers (Origin, Authorization, cookies...).</summary>
        public HttpHeaders Headers { get; init; } = new();

        /// <summary>Largest message accepted; bigger ones close the connection with <see cref="WebSocketCloseStatus.MessageTooBig"/>.</summary>
        public int MaxMessageSize { get; init; } = 16 * 1024 * 1024;

        /// <summary>Sends a ping this often so idle connections survive proxies and NATs; null disables it.</summary>
        public TimeSpan? KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(30);

        /// <summary>How long <see cref="WebSocketClient.Close"/> waits for the server's close frame.</summary>
        public TimeSpan CloseTimeout { get; init; } = TimeSpan.FromSeconds(5);
    }

    public class WebSocketException : CommunicationException
    {
        public WebSocketException(string message, Exception? innerException = null) : base(message, innerException) { }
    }
}

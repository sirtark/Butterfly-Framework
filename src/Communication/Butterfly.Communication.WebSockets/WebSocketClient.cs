using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

using Butterfly.Communication.Http;

namespace Butterfly.Communication.WebSockets
{
    /// <summary>
    /// A WebSocket client connection. One thread may receive while others send: sends are serialized internally.
    /// </summary>
    public sealed class WebSocketClient : IDisposable
    {
        private const string AcceptGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
        private const byte OpContinuation = 0x0, OpText = 0x1, OpBinary = 0x2, OpClose = 0x8, OpPing = 0x9, OpPong = 0xA;
        private static readonly UTF8Encoding s_strictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        private readonly NetworkConnection _connection;
        private readonly WebSocketClientOptions _options;
        private readonly Lock _writeLock = new();
        private readonly Timer? _keepAlive;
        private volatile WebSocketState _state = WebSocketState.Open;

        private WebSocketClient(NetworkConnection connection, WebSocketClientOptions options, string? subProtocol, HttpHeaders responseHeaders)
        {
            _connection = connection;
            _options = options;
            SubProtocol = subProtocol;
            ResponseHeaders = responseHeaders;

            if (options.KeepAliveInterval is { } interval)
                _keepAlive = new Timer(_ => KeepAlive(), null, interval, interval);
        }

        public WebSocketState State => _state;

        /// <summary>The sub-protocol the server selected, if any.</summary>
        public string? SubProtocol { get; }

        public HttpHeaders ResponseHeaders { get; }

        public WebSocketCloseStatus? CloseStatus { get; private set; }

        public string? CloseDescription { get; private set; }

        /// <summary>Connects to a ws:// or wss:// URI and performs the opening handshake.</summary>
        /// <exception cref="WebSocketException">The server refused the upgrade or answered an invalid handshake.</exception>
        public static WebSocketClient Connect(string uri, WebSocketClientOptions? options = null, CancellationToken cancellationToken = default)
            => Connect(new Uri(uri, UriKind.Absolute), options, cancellationToken);

        public static WebSocketClient Connect(Uri uri, WebSocketClientOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (uri.Scheme is not ("ws" or "wss"))
                throw new ArgumentException($"'{uri}' is not a ws:// or wss:// URI.", nameof(uri));

            options ??= new WebSocketClientOptions();
            bool secure = uri.Scheme == "wss";
            var connection = NetworkConnection.Connect(HttpWire.ConnectHost(uri), uri.Port, options.Connection, secure, cancellationToken: cancellationToken);

            try
            {
                string key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
                var headers = new HttpHeaders();
                headers.AddRange(options.Headers);
                headers.Set("Upgrade", "websocket");
                headers.Set("Connection", "Upgrade");
                headers.Set("Sec-WebSocket-Key", key);
                headers.Set("Sec-WebSocket-Version", "13");
                if (options.SubProtocols.Count > 0)
                    headers.Set("Sec-WebSocket-Protocol", string.Join(", ", options.SubProtocols));

                HttpWire.WriteRequestHead(connection.Stream, Http.HttpMethod.Get, uri, headers);
                connection.Flush();

                HttpResponseHead head = HttpWire.ReadResponseHead(connection.Reader);
                string? subProtocol = ValidateHandshake(head, key, options);
                return new WebSocketClient(connection, options, subProtocol, head.Headers);
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }

        public static Task<WebSocketClient> ConnectAsync(string uri, WebSocketClientOptions? options = null, CancellationToken cancellationToken = default)
            => Task.Run(() => Connect(uri, options, cancellationToken), cancellationToken);

        public void SendText(string text) => Send(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text);

        public void SendBinary(ReadOnlySpan<byte> data) => Send(data, WebSocketMessageType.Binary);

        public void Send(ReadOnlySpan<byte> data, WebSocketMessageType type)
        {
            EnsureOpen();
            WriteFrame(type == WebSocketMessageType.Text ? OpText : OpBinary, data);
        }

        public Task SendTextAsync(string text, CancellationToken cancellationToken = default)
            => _connection.RunAsync(() => SendText(text), cancellationToken);

        public Task SendBinaryAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
            => _connection.RunAsync(() => SendBinary(data.Span), cancellationToken);

        public void Ping(ReadOnlySpan<byte> payload = default)
        {
            if (payload.Length > 125)
                throw new ArgumentException("A ping payload is at most 125 bytes.", nameof(payload));
            EnsureOpen();
            WriteFrame(OpPing, payload);
        }

        /// <summary>
        /// Waits for the next message. Pings are answered automatically. Returns null once the connection is
        /// closed (see <see cref="CloseStatus"/>).
        /// </summary>
        public WebSocketMessage? Receive()
        {
            if (_state == WebSocketState.Closed)
                return null;

            using var message = new MemoryStream();
            byte? messageOpcode = null;

            try
            {
                while (true)
                {
                    Frame frame = ReadFrame();

                    switch (frame.Opcode)
                    {
                        case OpPing:
                            if (_state == WebSocketState.Open)
                                WriteFrame(OpPong, frame.Payload);
                            continue;

                        case OpPong:
                            continue;

                        case OpClose:
                            HandleClose(frame.Payload);
                            return null;

                        case OpText or OpBinary:
                            if (messageOpcode is not null)
                                return Fail(WebSocketCloseStatus.ProtocolError, "A new message started before the previous one ended.");
                            messageOpcode = frame.Opcode;
                            break;

                        case OpContinuation:
                            if (messageOpcode is null)
                                return Fail(WebSocketCloseStatus.ProtocolError, "Continuation frame without a message.");
                            break;

                        default:
                            return Fail(WebSocketCloseStatus.ProtocolError, $"Unknown opcode {frame.Opcode}.");
                    }

                    if (message.Length + frame.Payload.Length > _options.MaxMessageSize)
                        return Fail(WebSocketCloseStatus.MessageTooBig, $"The message exceeds {_options.MaxMessageSize} bytes.");

                    message.Write(frame.Payload);
                    if (!frame.Fin)
                        continue;

                    byte[] data = message.ToArray();
                    if (messageOpcode == OpText)
                    {
                        try
                        {
                            s_strictUtf8.GetCharCount(data);
                        }
                        catch (DecoderFallbackException)
                        {
                            return Fail(WebSocketCloseStatus.InvalidPayload, "A text message is not valid UTF-8.");
                        }
                    }

                    return new WebSocketMessage(messageOpcode == OpText ? WebSocketMessageType.Text : WebSocketMessageType.Binary, data);
                }
            }
            catch (Exception ex) when (ex is IOException or EndOfStreamException)
            {
                MarkClosed(WebSocketCloseStatus.Abnormal, "The connection was lost.");
                if (_state == WebSocketState.CloseSent)
                    return null;
                throw new WebSocketException("The WebSocket connection was lost.", ex);
            }
        }

        public Task<WebSocketMessage?> ReceiveAsync(CancellationToken cancellationToken = default)
            => _connection.RunAsync(Receive, cancellationToken);

        /// <summary>Starts the closing handshake and waits (up to CloseTimeout) for the server to confirm it.</summary>
        public void Close(WebSocketCloseStatus status = WebSocketCloseStatus.Normal, string? description = null)
        {
            if (_state != WebSocketState.Open)
            {
                Dispose();
                return;
            }

            if (status is WebSocketCloseStatus.NoStatus or WebSocketCloseStatus.Abnormal)
                throw new ArgumentException($"{status} cannot be sent in a close frame.", nameof(status));

            SendClose(status, description);

            // Messages still in flight are discarded until the server's close frame arrives.
            _connection.Stream.ReadTimeout = (int)_options.CloseTimeout.TotalMilliseconds;
            try
            {
                while (Receive() is not null) { }
            }
            catch (WebSocketException)
            {
            }

            Dispose();
        }

        public Task CloseAsync(WebSocketCloseStatus status = WebSocketCloseStatus.Normal, string? description = null, CancellationToken cancellationToken = default)
            => Task.Run(() => Close(status, description), cancellationToken);

        public void Dispose()
        {
            _keepAlive?.Dispose();
            if (_state != WebSocketState.Closed)
                MarkClosed(WebSocketCloseStatus.Abnormal, null);
            _connection.Dispose();
        }

        private static string? ValidateHandshake(HttpResponseHead head, string key, WebSocketClientOptions options)
        {
            if (head.StatusCode != 101)
                throw new WebSocketException($"The server refused the WebSocket upgrade: {head.StatusCode} {head.ReasonPhrase}.");

            if (!head.Headers.HasToken("Upgrade", "websocket") || !head.Headers.HasToken("Connection", "upgrade"))
                throw new WebSocketException("The server did not switch to the WebSocket protocol.");

            string expected = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + AcceptGuid)));
            if (head.Headers.Get("Sec-WebSocket-Accept") != expected)
                throw new WebSocketException("The server answered with a wrong Sec-WebSocket-Accept.");

            // No extensions were offered, so none may be accepted (RFC 6455 4.1).
            if (head.Headers.Contains("Sec-WebSocket-Extensions"))
                throw new WebSocketException("The server enabled an extension that was not requested.");

            string? protocol = head.Headers.Get("Sec-WebSocket-Protocol");
            if (protocol is not null && !options.SubProtocols.Contains(protocol, StringComparer.Ordinal))
                throw new WebSocketException($"The server selected the sub-protocol '{protocol}', which was not offered.");

            return protocol;
        }

        private void HandleClose(byte[] payload)
        {
            WebSocketCloseStatus status = WebSocketCloseStatus.NoStatus;
            string? description = null;

            if (payload.Length == 1)
            {
                Fail(WebSocketCloseStatus.ProtocolError, "Invalid close frame.");
                return;
            }

            if (payload.Length >= 2)
            {
                status = (WebSocketCloseStatus)BinaryPrimitives.ReadUInt16BigEndian(payload);
                try
                {
                    description = s_strictUtf8.GetString(payload, 2, payload.Length - 2);
                }
                catch (DecoderFallbackException)
                {
                    Fail(WebSocketCloseStatus.InvalidPayload, "The close reason is not valid UTF-8.");
                    return;
                }
            }

            // Answer the server's close with the same status (unless we started the handshake).
            if (_state == WebSocketState.Open)
                SendClose(status == WebSocketCloseStatus.NoStatus ? WebSocketCloseStatus.Normal : status, null);

            MarkClosed(status, description);
            _connection.Dispose();
        }

        private WebSocketMessage? Fail(WebSocketCloseStatus status, string reason)
        {
            try
            {
                if (_state == WebSocketState.Open)
                    SendClose(status, reason);
            }
            catch (IOException)
            {
            }

            MarkClosed(status, reason);
            _connection.Dispose();
            throw new WebSocketException(reason);
        }

        private void SendClose(WebSocketCloseStatus status, string? description)
        {
            byte[] reason = Encoding.UTF8.GetBytes(description ?? "");
            byte[] payload = new byte[2 + Math.Min(reason.Length, 123)];
            BinaryPrimitives.WriteUInt16BigEndian(payload, (ushort)status);
            reason.AsSpan(0, payload.Length - 2).CopyTo(payload.AsSpan(2));

            WriteFrame(OpClose, payload);
            _state = WebSocketState.CloseSent;
        }

        private void MarkClosed(WebSocketCloseStatus status, string? description)
        {
            _state = WebSocketState.Closed;
            CloseStatus ??= status;
            CloseDescription ??= description;
            _keepAlive?.Dispose();
        }

        private void EnsureOpen()
        {
            if (_state != WebSocketState.Open)
                throw new InvalidOperationException($"The WebSocket is {_state}.");
        }

        private void KeepAlive()
        {
            try
            {
                if (_state == WebSocketState.Open)
                    WriteFrame(OpPing, []);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
            }
        }

        private void WriteFrame(byte opcode, ReadOnlySpan<byte> payload)
        {
            // Header: FIN + opcode, MASK + length (7, 7+16 or 7+64 bits), then the 4-byte masking key.
            int headerLength = 2 + (payload.Length <= 125 ? 0 : payload.Length <= ushort.MaxValue ? 2 : 8) + 4;
            byte[] frame = new byte[headerLength + payload.Length];

            frame[0] = (byte)(0x80 | opcode);
            if (payload.Length <= 125)
            {
                frame[1] = (byte)(0x80 | payload.Length);
            }
            else if (payload.Length <= ushort.MaxValue)
            {
                frame[1] = 0x80 | 126;
                BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), (ushort)payload.Length);
            }
            else
            {
                frame[1] = 0x80 | 127;
                BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(2), (ulong)payload.Length);
            }

            // Clients must mask every frame with a fresh unpredictable key (RFC 6455 5.3).
            Span<byte> mask = frame.AsSpan(headerLength - 4, 4);
            RandomNumberGenerator.Fill(mask);
            Span<byte> body = frame.AsSpan(headerLength);
            for (int i = 0; i < payload.Length; i++)
                body[i] = (byte)(payload[i] ^ mask[i & 3]);

            lock (_writeLock)
            {
                _connection.Write(frame);
                _connection.Flush();
            }
        }

        private Frame ReadFrame()
        {
            ProtocolReader reader = _connection.Reader;
            Span<byte> header = stackalloc byte[2];
            reader.ReadExactly(header);

            bool fin = (header[0] & 0x80) != 0;
            byte opcode = (byte)(header[0] & 0x0F);
            bool masked = (header[1] & 0x80) != 0;
            long length = header[1] & 0x7F;

            if ((header[0] & 0x70) != 0)
                Fail(WebSocketCloseStatus.ProtocolError, "Reserved bits are set but no extension was negotiated.");
            if (masked)
                Fail(WebSocketCloseStatus.ProtocolError, "The server sent a masked frame.");

            if (length == 126)
            {
                Span<byte> extended = stackalloc byte[2];
                reader.ReadExactly(extended);
                length = BinaryPrimitives.ReadUInt16BigEndian(extended);
            }
            else if (length == 127)
            {
                Span<byte> extended = stackalloc byte[8];
                reader.ReadExactly(extended);
                length = (long)BinaryPrimitives.ReadUInt64BigEndian(extended);
            }

            bool control = opcode >= 0x8;
            if (control && (!fin || length > 125))
                Fail(WebSocketCloseStatus.ProtocolError, "Control frames must be unfragmented and at most 125 bytes.");
            if (length > _options.MaxMessageSize || length < 0)
                Fail(WebSocketCloseStatus.MessageTooBig, $"A frame exceeds {_options.MaxMessageSize} bytes.");

            return new Frame(fin, opcode, reader.ReadBytes((int)length));
        }

        private readonly record struct Frame(bool Fin, byte Opcode, byte[] Payload);
    }
}

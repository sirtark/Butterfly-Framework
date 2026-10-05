using Butterfly.Chrysalis.Http.Internal;
using Butterfly.Networking.Sockets;
using Microsoft.Extensions.Logging;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Butterfly.Chrysalis.Http.Http2
{
    /// <summary>A connection error: GOAWAY is sent with <see cref="Code"/> and the connection is closed.</summary>
    internal sealed class Http2ConnectionException(uint code, string message) : Exception(message)
    {
        public uint Code { get; } = code;
    }

    /// <summary>
    /// HTTP/2 (RFC 9113) on one connection. A dedicated thread reads frames; each complete request runs on the thread
    /// pool and writes its response under a write lock, respecting the flow-control windows of the peer.
    /// </summary>
    internal sealed class Http2Connection
    {
        private const byte FrameData = 0, FrameHeaders = 1, FramePriority = 2, FrameRstStream = 3, FrameSettings = 4,
            FramePushPromise = 5, FramePing = 6, FrameGoAway = 7, FrameWindowUpdate = 8, FrameContinuation = 9;
        private const byte FlagEndStream = 0x1, FlagAck = 0x1, FlagEndHeaders = 0x4, FlagPadded = 0x8, FlagPriority = 0x20;
        internal const uint NoError = 0, ProtocolError = 1, InternalError = 2, FlowControlError = 3, StreamClosed = 5,
            FrameSizeError = 6, RefusedStream = 7, Cancel = 8, CompressionError = 9, EnhanceYourCalm = 0xb;

        private const int MaxFrameSize = 16_384;
        private const int DefaultWindow = 65_535;
        private const int MaxWindow = int.MaxValue;
        // Bigger receive windows than the default 64 KiB, so uploads do not wait a round trip every 64 KiB.
        private const int StreamReceiveWindow = 1 << 20;
        private const int ConnectionReceiveWindow = 8 << 20;

        private static readonly byte[] Preface = Encoding.ASCII.GetBytes("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n");
        private static readonly HashSet<string> ConnectionSpecific = ["connection", "keep-alive", "proxy-connection", "transfer-encoding", "upgrade"];

        private readonly HttpServer server;
        private readonly HttpServerOptions options;
        private readonly TcpSocket socket;
        private readonly InputBuffer input;
        private readonly bool isSecure;
        private readonly string remoteAddress;
        private readonly HpackDecoder decoder = new(4096);

        private readonly Lock writeLock = new();
        // Monitor (not Lock): writers wait on it for WINDOW_UPDATE.
        private readonly object flowLock = new();
        private readonly Dictionary<int, Http2Stream> streams = [];
        private readonly CancellationTokenSource closed = new();

        private int lastStreamId;
        private long connectionSendWindow = DefaultWindow;
        private int peerInitialWindow = DefaultWindow;
        private int peerMaxFrameSize = MaxFrameSize;
        private int connectionReceiveWindow = ConnectionReceiveWindow;
        private bool goingAway;
        private int activeHandlers;

        // A header block split over HEADERS and CONTINUATION frames.
        private int continuationStream;
        private bool continuationEndsStream;
        private List<byte>? headerBlock;

        public Http2Connection(HttpServer server, TcpSocket socket, InputBuffer input, bool isSecure, string remoteAddress)
        {
            this.server = server;
            options = server.Options;
            this.socket = socket;
            this.input = input;
            this.isSecure = isSecure;
            this.remoteAddress = remoteAddress;
        }

        public void Run(bool expectPreface)
        {
            try
            {
                socket.ReceiveTimeout = options.RequestTimeout;
                if (expectPreface)
                {
                    Span<byte> preface = stackalloc byte[Preface.Length];
                    input.ReadExactly(preface);
                    if (!preface.SequenceEqual(Preface))
                        return;
                }

                SendSettings();
                WriteFrame(FrameWindowUpdate, 0, 0, UInt32(ConnectionReceiveWindow - DefaultWindow));

                var first = true;
                while (!server.IsStopping)
                {
                    var (type, flags, streamId, payload) = ReadFrame();
                    // The client preface ends with a SETTINGS frame (RFC 9113 3.4).
                    if (first && type != FrameSettings)
                        throw new Http2ConnectionException(ProtocolError, "The connection must start with SETTINGS.");
                    first = false;
                    socket.ReceiveTimeout = options.KeepAliveTimeout;
                    Handle(type, flags, streamId, payload);
                }
                GoAway(NoError);
            }
            catch (Http2ConnectionException exception)
            {
                options.Logger.LogDebug("HTTP/2 connection from {Remote} failed: {Message}", remoteAddress, exception.Message);
                GoAway(exception.Code);
            }
            catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException)
            {
                // The peer went away or a read timed out.
            }
            finally
            {
                closed.Cancel();
                lock (flowLock)
                    Monitor.PulseAll(flowLock);
                lock (streams)
                {
                    foreach (var stream in streams.Values)
                        stream.Abort();
                }
                // Let running handlers finish writing (or fail) before the socket is closed by the caller.
                SpinWait.SpinUntil(() => Volatile.Read(ref activeHandlers) == 0, TimeSpan.FromSeconds(5));
            }
        }

        private (byte Type, byte Flags, int StreamId, byte[] Payload) ReadFrame()
        {
            Span<byte> header = stackalloc byte[9];
            input.ReadExactly(header);
            var length = header[0] << 16 | header[1] << 8 | header[2];
            var streamId = (int)(BinaryPrimitives.ReadUInt32BigEndian(header[5..]) & 0x7FFF_FFFF);
            if (length > MaxFrameSize)
                throw new Http2ConnectionException(FrameSizeError, $"A frame of {length} bytes exceeds SETTINGS_MAX_FRAME_SIZE.");

            var payload = length == 0 ? [] : new byte[length];
            input.ReadExactly(payload);
            return (header[3], header[4], streamId, payload);
        }

        private void Handle(byte type, byte flags, int streamId, byte[] payload)
        {
            if (headerBlock is not null && (type != FrameContinuation || streamId != continuationStream))
                throw new Http2ConnectionException(ProtocolError, "A header block was interrupted by another frame.");

            switch (type)
            {
                case FrameData: OnData(flags, streamId, payload); break;
                case FrameHeaders: OnHeaders(flags, streamId, payload); break;
                case FrameContinuation: OnContinuation(flags, streamId, payload); break;
                case FramePriority:
                    if (streamId == 0)
                        throw new Http2ConnectionException(ProtocolError, "PRIORITY on stream 0.");
                    if (payload.Length != 5)
                        ResetStream(streamId, FrameSizeError);
                    break;
                case FrameRstStream: OnRstStream(streamId, payload); break;
                case FrameSettings: OnSettings(flags, streamId, payload); break;
                case FramePushPromise: throw new Http2ConnectionException(ProtocolError, "Clients cannot push.");
                case FramePing:
                    if (streamId != 0)
                        throw new Http2ConnectionException(ProtocolError, "PING on a stream.");
                    if (payload.Length != 8)
                        throw new Http2ConnectionException(FrameSizeError, "PING must carry 8 bytes.");
                    if ((flags & FlagAck) == 0)
                        WriteFrame(FramePing, FlagAck, 0, payload);
                    break;
                case FrameGoAway:
                    if (streamId != 0)
                        throw new Http2ConnectionException(ProtocolError, "GOAWAY on a stream.");
                    goingAway = true;
                    break;
                case FrameWindowUpdate: OnWindowUpdate(streamId, payload); break;
                default:
                    break; // Unknown frame types are ignored (RFC 9113 4.1).
            }
        }

        // ------------------------------------------------------------------ requests

        private void OnHeaders(byte flags, int streamId, byte[] payload)
        {
            if (streamId == 0)
                throw new Http2ConnectionException(ProtocolError, "HEADERS on stream 0.");

            var fragment = Unpad(flags, payload);
            if ((flags & FlagPriority) != 0)
            {
                if (fragment.Length < 5)
                    throw new Http2ConnectionException(FrameSizeError, "HEADERS too short for its priority.");
                fragment = fragment[5..];
            }

            Http2Stream? existing;
            lock (streams)
                streams.TryGetValue(streamId, out existing);

            if (existing is null)
            {
                if (streamId % 2 == 0 || streamId <= lastStreamId)
                    throw new Http2ConnectionException(ProtocolError, $"Invalid new stream {streamId}.");
                lastStreamId = streamId;
            }
            else if (existing.EndReceived || (flags & FlagEndStream) == 0)
            {
                // A second header block can only be trailers that end the stream.
                throw new Http2ConnectionException(ProtocolError, $"Unexpected HEADERS on stream {streamId}.");
            }

            continuationStream = streamId;
            continuationEndsStream = (flags & FlagEndStream) != 0;
            headerBlock = [.. fragment.ToArray()];
            if ((flags & FlagEndHeaders) != 0)
                CompleteHeaders();
        }

        private void OnContinuation(byte flags, int streamId, byte[] payload)
        {
            if (headerBlock is null || streamId != continuationStream)
                throw new Http2ConnectionException(ProtocolError, "Unexpected CONTINUATION.");
            headerBlock.AddRange(payload);
            // An endless header block is a known denial of service (CVE-2024-27316 and friends).
            if (headerBlock.Count > options.MaxRequestHeadersSize * 2)
                throw new Http2ConnectionException(EnhanceYourCalm, "The header block is too large.");
            if ((flags & FlagEndHeaders) != 0)
                CompleteHeaders();
        }

        private void CompleteHeaders()
        {
            var block = headerBlock!.ToArray();
            var streamId = continuationStream;
            var endStream = continuationEndsStream;
            headerBlock = null;

            List<(string Name, string Value)> fields;
            try
            {
                // Always decoded, even for refused streams: the dynamic table must stay in sync with the client's.
                fields = decoder.Decode(block);
            }
            catch (Exception exception) when (exception is HpackException or IndexOutOfRangeException)
            {
                throw new Http2ConnectionException(CompressionError, exception.Message);
            }

            Http2Stream? stream;
            lock (streams)
                streams.TryGetValue(streamId, out stream);

            if (stream is not null)
            {
                // Trailers: accepted and ignored.
                Dispatch(stream);
                return;
            }

            if (goingAway || server.IsStopping)
            {
                ResetStream(streamId, RefusedStream);
                return;
            }

            stream = new Http2Stream(streamId, peerInitialWindow);
            lock (streams)
            {
                if (streams.Count >= options.Http2MaxConcurrentStreams)
                {
                    ResetStream(streamId, RefusedStream);
                    return;
                }
                streams[streamId] = stream;
            }

            if (fields.Sum(field => field.Name.Length + field.Value.Length + 32) > options.MaxRequestHeadersSize || fields.Count > options.MaxRequestHeaderCount)
            {
                stream.Rejection = Rejection(431, "The request headers are too large.");
            }
            else if (!TryReadRequestHeaders(fields, stream, out var problem))
            {
                ResetStream(streamId, ProtocolError, stream);
                options.Logger.LogDebug("Malformed HTTP/2 request on stream {Stream}: {Problem}", streamId, problem);
                return;
            }

            if (endStream)
                Dispatch(stream);
        }

        private static bool TryReadRequestHeaders(List<(string Name, string Value)> fields, Http2Stream stream, out string problem)
        {
            var regularSeen = false;
            foreach (var (name, value) in fields)
            {
                if (name.StartsWith(':'))
                {
                    if (regularSeen)
                    {
                        problem = "pseudo-header after a regular header";
                        return false;
                    }
                    ref var slot = ref PseudoHeaderSlot(name, stream);
                    if (System.Runtime.CompilerServices.Unsafe.IsNullRef(ref slot) || slot is not null)
                    {
                        problem = $"unknown or repeated pseudo-header {name}";
                        return false;
                    }
                    slot = value;
                    continue;
                }

                regularSeen = true;
                if (name.Any(char.IsUpper) || name.Length == 0 || !name.All(HttpServerHeaders.IsTokenChar))
                {
                    problem = $"invalid header name '{name}'";
                    return false;
                }
                if (ConnectionSpecific.Contains(name) || (name == "te" && value != "trailers"))
                {
                    problem = $"connection-specific header '{name}'";
                    return false;
                }
                if (value.AsSpan().IndexOfAny('\r', '\n', '\0') >= 0)
                {
                    problem = $"invalid value of '{name}'";
                    return false;
                }
                stream.Headers.AddUnchecked(name, value);
            }

            if (stream.Method is null || stream.Scheme is null || string.IsNullOrEmpty(stream.Path) || stream.Method == "CONNECT")
            {
                problem = "missing :method, :scheme or :path (CONNECT is not supported)";
                return false;
            }
            if (stream.Authority is not null && !stream.Headers.Contains("host"))
                stream.Headers.AddUnchecked("host", stream.Authority);

            problem = "";
            return true;
        }

        // The field of the stream that holds a pseudo-header, or a null reference for unknown ones.
        private static ref string? PseudoHeaderSlot(string name, Http2Stream stream)
        {
            switch (name)
            {
                case ":method": return ref stream.Method;
                case ":scheme": return ref stream.Scheme;
                case ":path": return ref stream.Path;
                case ":authority": return ref stream.Authority;
                default: return ref System.Runtime.CompilerServices.Unsafe.NullRef<string?>();
            }
        }

        private void OnData(byte flags, int streamId, byte[] payload)
        {
            if (streamId == 0)
                throw new Http2ConnectionException(ProtocolError, "DATA on stream 0.");

            // Flow control counts the whole payload, padding included.
            connectionReceiveWindow -= payload.Length;
            if (connectionReceiveWindow < 0)
                throw new Http2ConnectionException(FlowControlError, "The client exceeded the connection window.");
            if (payload.Length > 0)
            {
                connectionReceiveWindow += payload.Length;
                WriteFrame(FrameWindowUpdate, 0, 0, UInt32(payload.Length));
            }

            Http2Stream? stream;
            lock (streams)
                streams.TryGetValue(streamId, out stream);
            if (stream is null)
            {
                if (streamId > lastStreamId)
                    throw new Http2ConnectionException(ProtocolError, $"DATA on idle stream {streamId}.");
                ResetStream(streamId, StreamClosed);
                return;
            }
            if (stream.EndReceived)
            {
                ResetStream(streamId, StreamClosed, stream);
                return;
            }

            stream.ReceiveWindow -= payload.Length;
            if (stream.ReceiveWindow < 0)
            {
                ResetStream(streamId, FlowControlError, stream);
                return;
            }

            var data = Unpad(flags, payload);
            if (stream.Rejection is null)
            {
                if (stream.Body.Length + data.Length > options.MaxRequestBodySize)
                    stream.Rejection = Rejection(413, $"The request body is larger than {options.MaxRequestBodySize} bytes.");
                else
                    stream.Body.Write(data);
            }

            if ((flags & FlagEndStream) != 0)
            {
                Dispatch(stream);
            }
            else if (payload.Length > 0)
            {
                stream.ReceiveWindow += payload.Length;
                WriteFrame(FrameWindowUpdate, 0, streamId, UInt32(payload.Length));
            }
        }

        private void Dispatch(Http2Stream stream)
        {
            stream.EndReceived = true;
            Interlocked.Increment(ref activeHandlers);
            _ = Task.Run(async () =>
            {
                try
                {
                    var response = stream.Rejection ?? await Serve(stream).ConfigureAwait(false);
                    WriteResponse(stream, response);
                }
                catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException)
                {
                    // The connection or the stream went away while responding.
                }
                catch (Exception exception)
                {
                    options.Logger.LogError(exception, "HTTP/2 stream {Stream} failed.", stream.Id);
                    ResetStream(stream.Id, InternalError, stream);
                }
                finally
                {
                    lock (streams)
                        streams.Remove(stream.Id);
                    stream.Abort();
                    Interlocked.Decrement(ref activeHandlers);
                }
            });
        }

        private async Task<HttpServerResponse> Serve(Http2Stream stream)
        {
            var body = stream.Body.ToArray();
            if (stream.Headers["content-length"] is { } declared
                && (!long.TryParse(declared, NumberStyles.None, CultureInfo.InvariantCulture, out var length) || length != body.Length))
                return Rejection(400, "Content-Length does not match the body.");

            var request = new HttpServerRequest(stream.Method!, stream.Path!, "HTTP/2", isSecure, remoteAddress, stream.Headers, body);
            using var aborted = CancellationTokenSource.CreateLinkedTokenSource(stream.Aborted.Token, closed.Token);
            return await server.DispatchAsync(request, aborted.Token).ConfigureAwait(false);
        }

        // ------------------------------------------------------------------ responses

        private void WriteResponse(Http2Stream stream, HttpServerResponse response)
        {
            var hasTrailers = response.Trailers.Count > 0;
            var body = HttpStatus.HasNoBody(response.StatusCode) ? ReadOnlyMemory<byte>.Empty : response.Body;

            var block = new List<byte>(128);
            HpackEncoder.Encode(block, ":status", response.StatusCode.ToString(CultureInfo.InvariantCulture));
            HpackEncoder.Encode(block, "date", DateTimeOffset.UtcNow.ToString("R", CultureInfo.InvariantCulture));
            if (options.ServerHeader is not null && !response.Headers.Contains("server"))
                HpackEncoder.Encode(block, "server", options.ServerHeader);
            foreach (var (name, value) in response.Headers)
            {
                var lower = name.ToLowerInvariant();
                if (ConnectionSpecific.Contains(lower) || lower == "content-length")
                    continue;
                HpackEncoder.Encode(block, lower, value);
            }
            if (!hasTrailers && !HttpStatus.HasNoBody(response.StatusCode))
                HpackEncoder.Encode(block, "content-length", body.Length.ToString(CultureInfo.InvariantCulture));

            WriteHeaderBlock(stream, block, endStream: body.IsEmpty && !hasTrailers);

            var offset = 0;
            while (offset < body.Length)
            {
                var size = ReserveWindow(stream, Math.Min(body.Length - offset, peerMaxFrameSize));
                var last = offset + size == body.Length;
                WriteFrame(FrameData, last && !hasTrailers ? FlagEndStream : (byte)0, stream.Id, body.Span.Slice(offset, size), stream);
                offset += size;
            }

            if (hasTrailers)
            {
                var trailers = new List<byte>(64);
                foreach (var (name, value) in response.Trailers)
                    HpackEncoder.Encode(trailers, name.ToLowerInvariant(), value);
                WriteHeaderBlock(stream, trailers, endStream: true);
            }
        }

        // Waits until both windows allow sending something, and takes up to `wanted` bytes from them.
        private int ReserveWindow(Http2Stream stream, int wanted)
        {
            lock (flowLock)
            {
                while (true)
                {
                    if (stream.Aborted.IsCancellationRequested || closed.IsCancellationRequested)
                        throw new OperationCanceledException("The stream was closed while waiting for flow control.");

                    var available = Math.Min(connectionSendWindow, stream.SendWindow);
                    if (available > 0)
                    {
                        var size = (int)Math.Min(available, wanted);
                        connectionSendWindow -= size;
                        stream.SendWindow -= size;
                        return size;
                    }
                    Monitor.Wait(flowLock, TimeSpan.FromSeconds(1));
                }
            }
        }

        private void WriteHeaderBlock(Http2Stream stream, List<byte> block, bool endStream)
        {
            var bytes = block.ToArray();
            // HEADERS followed by CONTINUATION frames must not be interleaved with other frames: one lock for all of them.
            lock (writeLock)
            {
                var offset = 0;
                var first = true;
                do
                {
                    var size = Math.Min(bytes.Length - offset, peerMaxFrameSize);
                    var last = offset + size == bytes.Length;
                    byte flags = (byte)((last ? FlagEndHeaders : 0) | (first && endStream ? FlagEndStream : 0));
                    WriteFrameLocked(first ? FrameHeaders : FrameContinuation, flags, stream.Id, bytes.AsSpan(offset, size));
                    offset += size;
                    first = false;
                }
                while (offset < bytes.Length);
            }
        }

        // ------------------------------------------------------------------ control

        private void OnSettings(byte flags, int streamId, byte[] payload)
        {
            if (streamId != 0)
                throw new Http2ConnectionException(ProtocolError, "SETTINGS on a stream.");
            if ((flags & FlagAck) != 0)
            {
                if (payload.Length != 0)
                    throw new Http2ConnectionException(FrameSizeError, "SETTINGS acknowledgement with a payload.");
                return;
            }
            if (payload.Length % 6 != 0)
                throw new Http2ConnectionException(FrameSizeError, "Malformed SETTINGS.");

            for (var offset = 0; offset < payload.Length; offset += 6)
            {
                var id = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(offset));
                var value = BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(offset + 2));
                switch (id)
                {
                    case 2 when value > 1: // ENABLE_PUSH
                        throw new Http2ConnectionException(ProtocolError, "Invalid SETTINGS_ENABLE_PUSH.");
                    case 4: // INITIAL_WINDOW_SIZE: applies to every open stream (RFC 9113 6.9.2).
                        if (value > MaxWindow)
                            throw new Http2ConnectionException(FlowControlError, "SETTINGS_INITIAL_WINDOW_SIZE is too large.");
                        lock (flowLock)
                        {
                            var delta = (int)value - peerInitialWindow;
                            peerInitialWindow = (int)value;
                            lock (streams)
                            {
                                foreach (var stream in streams.Values)
                                    stream.SendWindow += delta;
                            }
                            Monitor.PulseAll(flowLock);
                        }
                        break;
                    case 5: // MAX_FRAME_SIZE
                        if (value is < MaxFrameSize or > 16_777_215)
                            throw new Http2ConnectionException(ProtocolError, "Invalid SETTINGS_MAX_FRAME_SIZE.");
                        peerMaxFrameSize = (int)value;
                        break;
                    // HEADER_TABLE_SIZE needs nothing: the encoder never uses the dynamic table.
                }
            }
            WriteFrame(FrameSettings, FlagAck, 0, []);
        }

        private void OnWindowUpdate(int streamId, byte[] payload)
        {
            if (payload.Length != 4)
                throw new Http2ConnectionException(FrameSizeError, "WINDOW_UPDATE must carry 4 bytes.");
            var increment = (int)(BinaryPrimitives.ReadUInt32BigEndian(payload) & 0x7FFF_FFFF);

            lock (flowLock)
            {
                if (streamId == 0)
                {
                    if (increment == 0)
                        throw new Http2ConnectionException(ProtocolError, "WINDOW_UPDATE of 0.");
                    if (connectionSendWindow + increment > MaxWindow)
                        throw new Http2ConnectionException(FlowControlError, "The connection window overflowed.");
                    connectionSendWindow += increment;
                }
                else
                {
                    Http2Stream? stream;
                    lock (streams)
                        streams.TryGetValue(streamId, out stream);
                    if (stream is null)
                    {
                        if (streamId > lastStreamId)
                            throw new Http2ConnectionException(ProtocolError, $"WINDOW_UPDATE on idle stream {streamId}.");
                        return; // A stream that just finished.
                    }
                    if (increment == 0)
                    {
                        ResetStream(streamId, ProtocolError, stream);
                        return;
                    }
                    if (stream.SendWindow + increment > MaxWindow)
                    {
                        ResetStream(streamId, FlowControlError, stream);
                        return;
                    }
                    stream.SendWindow += increment;
                }
                Monitor.PulseAll(flowLock);
            }
        }

        private void OnRstStream(int streamId, byte[] payload)
        {
            if (streamId == 0)
                throw new Http2ConnectionException(ProtocolError, "RST_STREAM on stream 0.");
            if (payload.Length != 4)
                throw new Http2ConnectionException(FrameSizeError, "RST_STREAM must carry 4 bytes.");
            if (streamId > lastStreamId)
                throw new Http2ConnectionException(ProtocolError, $"RST_STREAM on idle stream {streamId}.");

            Http2Stream? stream;
            lock (streams)
            {
                if (streams.Remove(streamId, out stream))
                    stream.Abort();
            }
            lock (flowLock)
                Monitor.PulseAll(flowLock);
        }

        private void SendSettings()
        {
            var settings = new byte[18];
            Setting(settings, 0, 3, (uint)options.Http2MaxConcurrentStreams);        // MAX_CONCURRENT_STREAMS
            Setting(settings, 6, 4, StreamReceiveWindow);                              // INITIAL_WINDOW_SIZE
            Setting(settings, 12, 6, (uint)options.MaxRequestHeadersSize);             // MAX_HEADER_LIST_SIZE
            WriteFrame(FrameSettings, 0, 0, settings);

            static void Setting(byte[] buffer, int offset, ushort id, uint value)
            {
                BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset), id);
                BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(offset + 2), value);
            }
        }

        private void ResetStream(int streamId, uint code, Http2Stream? stream = null)
        {
            if (stream is not null)
            {
                lock (streams)
                    streams.Remove(streamId);
                stream.Abort();
            }
            try
            {
                WriteFrame(FrameRstStream, 0, streamId, UInt32(code));
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
            }
        }

        private void GoAway(uint code)
        {
            var payload = new byte[8];
            BinaryPrimitives.WriteUInt32BigEndian(payload, (uint)lastStreamId);
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4), code);
            try
            {
                WriteFrame(FrameGoAway, 0, 0, payload);
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or SocketException)
            {
            }
        }

        private void WriteFrame(byte type, byte flags, int streamId, ReadOnlySpan<byte> payload, Http2Stream? stream = null)
        {
            lock (writeLock)
            {
                if (stream is not null && stream.Aborted.IsCancellationRequested)
                    throw new OperationCanceledException("The stream was reset.");
                WriteFrameLocked(type, flags, streamId, payload);
            }
        }

        private void WriteFrameLocked(byte type, byte flags, int streamId, ReadOnlySpan<byte> payload)
        {
            var frame = new byte[9 + payload.Length];
            frame[0] = (byte)(payload.Length >> 16);
            frame[1] = (byte)(payload.Length >> 8);
            frame[2] = (byte)payload.Length;
            frame[3] = type;
            frame[4] = flags;
            BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(5), (uint)streamId);
            payload.CopyTo(frame.AsSpan(9));
            input.Stream.Write(frame);
            input.Stream.Flush();
        }

        private static ReadOnlySpan<byte> Unpad(byte flags, byte[] payload)
        {
            if ((flags & FlagPadded) == 0)
                return payload;
            if (payload.Length == 0 || payload[0] >= payload.Length)
                throw new Http2ConnectionException(ProtocolError, "Invalid padding.");
            return payload.AsSpan(1, payload.Length - 1 - payload[0]);
        }

        private static byte[] UInt32(long value)
        {
            var bytes = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)value);
            return bytes;
        }

        private static HttpServerResponse Rejection(int status, string message)
        {
            var response = new HttpServerResponse { StatusCode = status };
            response.Write(message, "text/plain; charset=utf-8");
            return response;
        }

        private sealed class Http2Stream(int id, int sendWindow)
        {
            public int Id { get; } = id;
            public string? Method;
            public string? Scheme;
            public string? Path;
            public string? Authority;
            public HttpServerHeaders Headers { get; } = new();
            public MemoryStream Body { get; } = new();
            public bool EndReceived;
            public long SendWindow = sendWindow;
            public int ReceiveWindow = StreamReceiveWindow;
            /// <summary>An error response decided while reading the request (too large...), sent instead of running the handler.</summary>
            public HttpServerResponse? Rejection;
            public CancellationTokenSource Aborted { get; } = new();

            public void Abort()
            {
                try
                {
                    Aborted.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }
    }
}

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Butterfly.Communication.Mqtt
{
    /// <summary>
    /// MQTT 3.1.1 client. A background thread receives packets: <see cref="MessageReceived"/> is raised on it, so
    /// handlers should be quick. Publish, Subscribe and Unsubscribe can be called from any thread.
    /// </summary>
    public sealed class MqttClient(ConnectionOptions? options = null) : ProtocolClient(options)
    {
        public const int DefaultPort = 1883;
        public const int ImplicitTlsPort = 8883;

        private const byte Connect_ = 0x10, ConnAck = 0x20, Publish_ = 0x30, PubAck = 0x40, PubRec = 0x50, PubRel = 0x60, PubComp = 0x70;
        private const byte SubscribeType = 0x80, SubAck = 0x90, UnsubscribeType = 0xA0, UnsubAck = 0xB0, PingReq = 0xC0, PingResp = 0xD0, DisconnectType = 0xE0;

        private readonly Lock _writeLock = new();
        private readonly ConcurrentDictionary<(byte Type, ushort Id), TaskCompletionSource<byte[]>> _pending = new();
        private readonly ConcurrentDictionary<ushort, byte> _incomingExactlyOnce = new();
        private MqttClientOptions _options = new();
        private Thread? _reader;
        private Timer? _keepAlive;
        private long _lastSent;
        private int _nextPacketId;
        private volatile bool _closing;

        /// <summary>Raised on the receiving thread for every application message. Exceptions thrown by handlers are ignored.</summary>
        public event Action<MqttMessage>? MessageReceived;

        /// <summary>Raised once when the connection ends; the exception is null after <see cref="Disconnect"/>.</summary>
        public event Action<Exception?>? Disconnected;

        /// <summary>The broker still had a session for this client id (only with CleanSession = false).</summary>
        public bool SessionPresent { get; private set; }

        public string? ClientId { get; private set; }

        /// <param name="tls"><see cref="TlsMode.Auto"/>: TLS on 8883, plain on 1883. MQTT has no STARTTLS.</param>
        public void Connect(string host, int port = DefaultPort, TlsMode tls = TlsMode.Auto, MqttClientOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (tls is TlsMode.StartTls or TlsMode.StartTlsWhenAvailable)
                throw new ArgumentException("MQTT has no STARTTLS: use TlsMode.Implicit (port 8883) or TlsMode.None.", nameof(tls));

            _options = options ?? new MqttClientOptions();
            bool implicitTls = tls == TlsMode.Implicit || (tls == TlsMode.Auto && port == ImplicitTlsPort);
            if (_options.ClientId.Length == 0 && !_options.CleanSession)
                throw new ArgumentException("A persistent session (CleanSession = false) needs a ClientId.", nameof(options));

            ClientId = _options.ClientId.Length > 0 ? _options.ClientId : "butterfly-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
            _closing = false;

            NetworkConnection connection = OpenConnection(host, port, implicitTls, cancellationToken);
            try
            {
                if (_options.UserName is not null || _options.Password is not null)
                    EnsureCanSendCredentials();

                WritePacket(Connect_, EncodeConnect());
                byte[] connAck = ReadPacket(out byte type);
                if (type != ConnAck || connAck.Length < 2)
                    throw new MqttException("The broker did not answer CONNECT with CONNACK.");

                var code = (MqttConnectReturnCode)connAck[1];
                if (code != MqttConnectReturnCode.Accepted)
                    throw new MqttException($"The broker refused the connection: {code}.") { ReturnCode = code };

                SessionPresent = (connAck[0] & 1) != 0;

                // Silence longer than 1.5 × keep-alive means the broker is gone (MQTT 3.1.1, 3.1.2.10).
                connection.Stream.ReadTimeout = _options.KeepAlive > TimeSpan.Zero ? (int)(_options.KeepAlive.TotalMilliseconds * 1.5) : Timeout.Infinite;

                _reader = new Thread(ReadLoop) { IsBackground = true, Name = $"MQTT {ClientId}" };
                _reader.Start();

                if (_options.KeepAlive > TimeSpan.Zero)
                {
                    TimeSpan period = TimeSpan.FromMilliseconds(Math.Max(100, _options.KeepAlive.TotalMilliseconds / 4));
                    _keepAlive = new Timer(_ => KeepAlive(), null, period, period);
                }
            }
            catch
            {
                CloseConnection();
                throw;
            }
        }

        public Task ConnectAsync(string host, int port = DefaultPort, TlsMode tls = TlsMode.Auto, MqttClientOptions? options = null, CancellationToken cancellationToken = default)
            => Task.Run(() => Connect(host, port, tls, options, cancellationToken), cancellationToken);

        public void Publish(string topic, string payload, MqttQos qos = MqttQos.AtMostOnce, bool retain = false)
            => Publish(new MqttMessage(topic, payload, qos, retain));

        public void Publish(string topic, byte[] payload, MqttQos qos = MqttQos.AtMostOnce, bool retain = false)
            => Publish(new MqttMessage(topic, payload, qos, retain));

        /// <summary>Publishes and, for QoS 1 and 2, waits until the broker has acknowledged the message.</summary>
        public void Publish(MqttMessage message)
        {
            MqttTopic.ValidateTopic(message.Topic);
            EnsureConnected();

            byte flags = (byte)(((byte)message.Qos << 1) | (message.Retain ? 1 : 0));
            if (message.Qos == MqttQos.AtMostOnce)
            {
                WritePacket((byte)(Publish_ | flags), EncodePublish(message, 0));
                return;
            }

            ushort id = NextPacketId();
            if (message.Qos == MqttQos.AtLeastOnce)
            {
                Request(PubAck, id, (byte)(Publish_ | flags), EncodePublish(message, id));
                return;
            }

            // QoS 2: PUBLISH → PUBREC, PUBREL → PUBCOMP.
            Request(PubRec, id, (byte)(Publish_ | flags), EncodePublish(message, id));
            Request(PubComp, id, PubRel | 0x02, PacketId(id));
        }

        public Task PublishAsync(MqttMessage message, CancellationToken cancellationToken = default)
            => Task.Run(() => Publish(message), cancellationToken);

        public MqttQos Subscribe(string filter, MqttQos qos = MqttQos.AtMostOnce)
            => Subscribe((filter, qos))[0] ?? throw new MqttException($"The broker refused the subscription to '{filter}'.");

        /// <summary>Subscribes to several filters at once. Returns the QoS granted to each, or null where the broker refused.</summary>
        public IReadOnlyList<MqttQos?> Subscribe(params (string Filter, MqttQos Qos)[] subscriptions)
        {
            if (subscriptions.Length == 0)
                throw new ArgumentException("Nothing to subscribe to.", nameof(subscriptions));
            EnsureConnected();

            var payload = new List<byte>();
            foreach (var (filter, qos) in subscriptions)
            {
                MqttTopic.ValidateFilter(filter);
                WriteString(payload, filter);
                payload.Add((byte)qos);
            }

            ushort id = NextPacketId();
            byte[] ack = Request(SubAck, id, SubscribeType | 0x02, [.. PacketId(id), .. payload]);
            return [.. ack.Skip(2).Select(code => code == 0x80 ? (MqttQos?)null : (MqttQos)code)];
        }

        public void Unsubscribe(params string[] filters)
        {
            if (filters.Length == 0)
                throw new ArgumentException("Nothing to unsubscribe from.", nameof(filters));
            EnsureConnected();

            ushort id = NextPacketId();
            var payload = new List<byte>(PacketId(id));
            foreach (string filter in filters)
                WriteString(payload, filter);

            Request(UnsubAck, id, UnsubscribeType | 0x02, [.. payload]);
        }

        public Task<IReadOnlyList<MqttQos?>> SubscribeAsync(IEnumerable<(string Filter, MqttQos Qos)> subscriptions, CancellationToken cancellationToken = default)
            => Task.Run(() => Subscribe([.. subscriptions]), cancellationToken);

        /// <summary>Sends DISCONNECT (so the will is not published) and closes the connection.</summary>
        public void Disconnect() => Dispose();

        protected override void SayGoodbye()
        {
            _closing = true;
            try
            {
                WritePacket(DisconnectType, []);
            }
            finally
            {
                // Wake the receiving thread, which is blocked reading.
                Connection.Abort();
            }
        }

        protected override void Dispose(bool disposing)
        {
            _closing = true;
            _keepAlive?.Dispose();
            bool wasConnected = IsConnected;
            base.Dispose(disposing);

            if (_reader is not null && _reader != Thread.CurrentThread)
                _reader.Join(TimeSpan.FromSeconds(2));

            FailPending(new MqttException("The client was disconnected."));
            if (wasConnected)
                Disconnected?.Invoke(null);
        }

        // --- receiving ---------------------------------------------------------------------------------------

        private void ReadLoop()
        {
            try
            {
                while (!_closing)
                {
                    byte[] body = ReadPacket(out byte header);
                    Dispatch(header, body);
                }
            }
            catch (Exception ex) when (!_closing)
            {
                FailPending(new MqttException("The connection to the broker was lost.", ex));
                _keepAlive?.Dispose();
                CloseConnection();
                Disconnected?.Invoke(ex);
            }
            catch (Exception) when (_closing)
            {
                // Disconnecting: the read was interrupted on purpose.
            }
        }

        private void Dispatch(byte header, byte[] body)
        {
            byte type = (byte)(header & 0xF0);
            switch (type)
            {
                case Publish_:
                    ReceivePublish(header, body);
                    break;

                case PubRel:
                    ushort released = BinaryPrimitives.ReadUInt16BigEndian(body);
                    _incomingExactlyOnce.TryRemove(released, out _);
                    WritePacket(PubComp, PacketId(released));
                    break;

                case PubAck or PubRec or PubComp or SubAck or UnsubAck:
                    ushort id = BinaryPrimitives.ReadUInt16BigEndian(body);
                    if (_pending.TryRemove((type, id), out var waiter))
                        waiter.TrySetResult(body);
                    break;

                case PingResp:
                    break;

                default:
                    throw new ProtocolViolationException($"Unexpected MQTT packet type 0x{type:X2}.");
            }
        }

        private void ReceivePublish(byte header, byte[] body)
        {
            var qos = (MqttQos)((header >> 1) & 0x03);
            int topicLength = BinaryPrimitives.ReadUInt16BigEndian(body);
            string topic = Encoding.UTF8.GetString(body, 2, topicLength);
            int offset = 2 + topicLength;

            ushort id = 0;
            if (qos != MqttQos.AtMostOnce)
            {
                id = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(offset));
                offset += 2;
            }

            var message = new MqttMessage(topic, body[offset..], qos, (header & 0x01) != 0) { Duplicate = (header & 0x08) != 0 };

            switch (qos)
            {
                case MqttQos.AtMostOnce:
                    Deliver(message);
                    break;
                case MqttQos.AtLeastOnce:
                    Deliver(message);
                    WritePacket(PubAck, PacketId(id));
                    break;
                case MqttQos.ExactlyOnce:
                    // Deliver once; redeliveries with the same id before PUBREL are duplicates.
                    if (_incomingExactlyOnce.TryAdd(id, 0))
                        Deliver(message);
                    WritePacket(PubRec, PacketId(id));
                    break;
            }
        }

        private void Deliver(MqttMessage message)
        {
            try
            {
                MessageReceived?.Invoke(message);
            }
            catch (Exception)
            {
                // A faulty handler must not bring the connection down.
            }
        }

        // --- sending -----------------------------------------------------------------------------------------

        private byte[] Request(byte ackType, ushort id, byte header, byte[] body)
        {
            var waiter = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[(ackType, id)] = waiter;

            try
            {
                WritePacket(header, body);
                if (!waiter.Task.Wait(_options.AcknowledgementTimeout))
                    throw new MqttException($"The broker did not acknowledge packet {id} in time.");
                return waiter.Task.Result;
            }
            catch (AggregateException ex) when (ex.InnerException is not null)
            {
                throw ex.InnerException;
            }
            finally
            {
                _pending.TryRemove((ackType, id), out _);
            }
        }

        private void WritePacket(byte header, byte[] body)
        {
            // Fixed header: type and flags, then the remaining length in 7-bit groups (at most 4 bytes).
            var packet = new List<byte>(body.Length + 5) { header };
            int length = body.Length;
            do
            {
                byte digit = (byte)(length % 128);
                length /= 128;
                packet.Add(length > 0 ? (byte)(digit | 0x80) : digit);
            }
            while (length > 0);
            packet.AddRange(body);

            lock (_writeLock)
            {
                Connection.Write(packet.ToArray());
                Connection.Flush();
            }

            Interlocked.Exchange(ref _lastSent, Environment.TickCount64);
        }

        private byte[] ReadPacket(out byte header)
        {
            ProtocolReader reader = Connection.Reader;
            Span<byte> one = stackalloc byte[1];

            reader.ReadExactly(one);
            header = one[0];

            int length = 0, multiplier = 1;
            for (int i = 0; ; i++)
            {
                if (i == 4)
                    throw new ProtocolViolationException("Invalid MQTT remaining length.");
                reader.ReadExactly(one);
                length += (one[0] & 0x7F) * multiplier;
                multiplier *= 128;
                if ((one[0] & 0x80) == 0)
                    break;
            }

            return reader.ReadBytes(length);
        }

        private void KeepAlive()
        {
            try
            {
                if (IsConnected && !_closing && Environment.TickCount64 - Interlocked.Read(ref _lastSent) >= _options.KeepAlive.TotalMilliseconds * 0.75)
                    WritePacket(PingReq, []);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
            }
        }

        private byte[] EncodeConnect()
        {
            var body = new List<byte>();
            WriteString(body, "MQTT");
            body.Add(4); // protocol level 3.1.1

            byte flags = (byte)(_options.CleanSession ? 0x02 : 0);
            if (_options.Will is { } will)
                flags |= (byte)(0x04 | ((byte)will.Qos << 3) | (will.Retain ? 0x20 : 0));
            if (_options.UserName is not null)
                flags |= 0x80;
            if (_options.Password is not null)
                flags |= 0x40;
            body.Add(flags);

            ushort keepAlive = (ushort)Math.Min(ushort.MaxValue, _options.KeepAlive.TotalSeconds);
            body.AddRange([(byte)(keepAlive >> 8), (byte)keepAlive]);

            WriteString(body, ClientId!);
            if (_options.Will is { } lastWill)
            {
                MqttTopic.ValidateTopic(lastWill.Topic);
                WriteString(body, lastWill.Topic);
                WriteBytes(body, lastWill.Payload);
            }
            if (_options.UserName is not null)
                WriteString(body, _options.UserName);
            if (_options.Password is not null)
                WriteBytes(body, Encoding.UTF8.GetBytes(_options.Password));

            return [.. body];
        }

        private static byte[] EncodePublish(MqttMessage message, ushort id)
        {
            var body = new List<byte>(message.Payload.Length + message.Topic.Length + 4);
            WriteString(body, message.Topic);
            if (message.Qos != MqttQos.AtMostOnce)
                body.AddRange(PacketId(id));
            body.AddRange(message.Payload);
            return [.. body];
        }

        private ushort NextPacketId()
        {
            // 1..65535, skipping ids still waiting for an acknowledgement.
            while (true)
            {
                ushort id = (ushort)(Interlocked.Increment(ref _nextPacketId) % ushort.MaxValue + 1);
                if (!_pending.Keys.Any(k => k.Id == id))
                    return id;
            }
        }

        private void FailPending(Exception error)
        {
            foreach (var key in _pending.Keys)
            {
                if (_pending.TryRemove(key, out var waiter))
                    waiter.TrySetException(error);
            }
        }

        private void EnsureConnected()
        {
            if (!IsConnected || _closing)
                throw new InvalidOperationException("The client is not connected.");
        }

        private static byte[] PacketId(ushort id) => [(byte)(id >> 8), (byte)id];

        private static void WriteString(List<byte> buffer, string value) => WriteBytes(buffer, Encoding.UTF8.GetBytes(value));

        private static void WriteBytes(List<byte> buffer, byte[] value)
        {
            buffer.Add((byte)(value.Length >> 8));
            buffer.Add((byte)value.Length);
            buffer.AddRange(value);
        }
    }
}

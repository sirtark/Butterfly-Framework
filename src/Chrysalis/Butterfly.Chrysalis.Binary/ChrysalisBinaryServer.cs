using Butterfly.Serialization;
using Butterfly.Serialization.Protobuf;
using Butterfly.Communication;
using Butterfly.Networking.Sockets;
using Microsoft.Extensions.Logging;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Butterfly.Chrysalis.Binary
{
    public sealed class ChrysalisBinaryServerOptions
    {
        /// <summary>Addresses to listen on, each with an optional TLS certificate.</summary>
        public IList<(SocketAddress Address, X509Certificate2? Certificate)> Endpoints { get; } = [];
        public int MaxFrameSize { get; set; } = 16 * 1024 * 1024;
        public int MaxConcurrentCallsPerConnection { get; set; } = 100;
        public int MaxConcurrentConnections { get; set; } = 1000;
        /// <summary>How long a connection may stay silent (no call, no ping) before it is closed.</summary>
        public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(5);
    }

    /// <summary>Serves the services of a <see cref="ChrysalisServer"/> with the Chrysalis binary protocol.</summary>
    public sealed class ChrysalisBinaryServer : IAsyncDisposable
    {
        public const string Protocol = "Binary";

        private readonly ChrysalisServer chrysalis;
        private readonly List<(TcpSocket Socket, Thread Thread)> listeners = [];
        private readonly Dictionary<long, TcpSocket> connections = [];
        private readonly CancellationTokenSource stopping = new();
        private long nextConnection;
        private int activeConnections;

        public ChrysalisBinaryServer(ChrysalisServer chrysalis, ChrysalisBinaryServerOptions options)
        {
            ArgumentNullException.ThrowIfNull(chrysalis);
            ArgumentNullException.ThrowIfNull(options);
            this.chrysalis = chrysalis;
            Options = options;
        }

        public ChrysalisBinaryServerOptions Options { get; }

        /// <summary>The addresses actually listening (with the real port when 0 was requested).</summary>
        public IReadOnlyList<SocketAddress> Endpoints { get; private set; } = [];

        public void Start()
        {
            if (Options.Endpoints.Count == 0)
                throw new InvalidOperationException("Add at least one endpoint to the options.");
            var bound = new List<SocketAddress>();
            foreach (var (address, certificate) in Options.Endpoints)
            {
                var socket = new TcpSocket(address.Family, new TcpSocketOptions { NoDelay = true });
                socket.Bind(address);
                socket.Listen();
                bound.Add(socket.LocalAddress);
                var thread = new Thread(() => AcceptLoop(socket, certificate)) { IsBackground = true, Name = "Chrysalis binary listener" };
                listeners.Add((socket, thread));
            }
            Endpoints = bound;
            foreach (var (_, thread) in listeners)
                thread.Start();
        }

        public async Task StopAsync()
        {
            if (stopping.IsCancellationRequested)
                return;
            stopping.Cancel();
            foreach (var (socket, _) in listeners)
                socket.Abort();
            lock (connections)
            {
                foreach (var connection in connections.Values)
                    connection.Abort();
            }
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (Volatile.Read(ref activeConnections) > 0 && DateTime.UtcNow < deadline)
                await Task.Delay(20).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync() => new(StopAsync());

        private void AcceptLoop(TcpSocket listener, X509Certificate2? certificate)
        {
            while (!stopping.IsCancellationRequested)
            {
                TcpSocket client;
                try
                {
                    client = listener.Accept();
                }
                catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
                {
                    if (stopping.IsCancellationRequested)
                        return;
                    continue;
                }

                if (Interlocked.Increment(ref activeConnections) > Options.MaxConcurrentConnections)
                {
                    Interlocked.Decrement(ref activeConnections);
                    client.Dispose();
                    continue;
                }
                var id = Interlocked.Increment(ref nextConnection);
                lock (connections)
                    connections[id] = client;
                new Thread(() => Serve(id, client, certificate)) { IsBackground = true, Name = "Chrysalis binary connection" }.Start();
            }
        }

        private void Serve(long id, TcpSocket socket, X509Certificate2? certificate)
        {
            Stream stream = new SocketStream(socket, ownsSocket: false);
            var writeLock = new Lock();
            var calls = new Dictionary<uint, CancellationTokenSource>();
            var remote = socket.RemoteAddress.ToString();
            try
            {
                socket.ReceiveTimeout = Options.IdleTimeout;
                if (certificate is not null)
                {
                    var tls = new SslStream(stream);
                    stream = tls;
                    tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, stopping.Token).GetAwaiter().GetResult();
                }

                Span<byte> handshake = stackalloc byte[5];
                if (!BinaryProtocol.ReadExactly(stream, handshake, allowEnd: true) || !BinaryProtocol.IsHandshake(handshake))
                    return;
                stream.Write(BinaryProtocol.Handshake());

                void Send(byte type, uint call, ReadOnlySpan<byte> payload)
                {
                    var frame = BinaryProtocol.Frame(type, call, payload);
                    lock (writeLock)
                    {
                        stream.Write(frame);
                        stream.Flush();
                    }
                }

                while (!stopping.IsCancellationRequested)
                {
                    if (BinaryProtocol.ReadFrame(stream, Options.MaxFrameSize) is not { } frame)
                        return;

                    switch (frame.Type)
                    {
                        case BinaryProtocol.Ping:
                            Send(BinaryProtocol.Pong, frame.Call, []);
                            break;
                        case BinaryProtocol.Cancel:
                            lock (calls)
                            {
                                if (calls.TryGetValue(frame.Call, out var cancellation))
                                    cancellation.Cancel();
                            }
                            break;
                        case BinaryProtocol.Request:
                            var cancel = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
                            bool accepted;
                            lock (calls)
                                accepted = calls.Count < Options.MaxConcurrentCallsPerConnection && calls.TryAdd(frame.Call, cancel);
                            if (!accepted)
                            {
                                cancel.Dispose();
                                Send(BinaryProtocol.Response, frame.Call, Error(ChrysalisStatus.ResourceExhausted, "Too many concurrent calls, or a call id in use."));
                                break;
                            }
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    Send(BinaryProtocol.Response, frame.Call, await Execute(frame.Payload, remote, cancel.Token).ConfigureAwait(false));
                                }
                                catch (Exception exception) when (exception is IOException or ObjectDisposedException)
                                {
                                }
                                finally
                                {
                                    lock (calls)
                                        calls.Remove(frame.Call);
                                    cancel.Dispose();
                                }
                            });
                            break;
                        default:
                            return; // Unknown frame: the peer does not speak this protocol version.
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or SocketException or InvalidDataException or AuthenticationException or ObjectDisposedException or OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                chrysalis.Options.Logger.LogError(exception, "A binary connection failed.");
            }
            finally
            {
                lock (calls)
                {
                    foreach (var cancellation in calls.Values)
                        cancellation.Cancel();
                }
                try
                {
                    stream.Dispose();
                }
                catch (Exception exception) when (exception is IOException or ObjectDisposedException)
                {
                }
                socket.Dispose();
                lock (connections)
                    connections.Remove(id);
                Interlocked.Decrement(ref activeConnections);
            }
        }

        private async Task<byte[]> Execute(byte[] payload, string remote, CancellationToken cancellationToken)
        {
            try
            {
                var position = 0;
                var name = BinaryProtocol.ReadString(payload, ref position);
                var operation = chrysalis.FindOperation(name)
                    ?? throw new ChrysalisException(ChrysalisStatus.Unimplemented, $"No operation named '{name}'.");

                var metadata = new List<(string, string)>();
                var count = BinaryProtocol.ReadUInt16(payload, ref position);
                for (var i = 0; i < count; i++)
                    metadata.Add((BinaryProtocol.ReadString(payload, ref position), BinaryProtocol.ReadString(payload, ref position)));

                object?[] arguments;
                try
                {
                    arguments = (object?[])ProtobufFormat.Instance.Deserialize(operation.ParametersType, payload.AsMemory(position), chrysalis.Options.Profile)!;
                }
                catch (SerializationException exception)
                {
                    throw ChrysalisException.InvalidInput(exception);
                }
                var context = new ChrysalisCallContext(operation, arguments, Protocol, cancellationToken) { RemoteAddress = remote };
                foreach (var (key, value) in metadata)
                    context.Headers[key] = value;

                var result = await chrysalis.InvokeAsync(context).ConfigureAwait(false);
                byte[] encoded;
                try
                {
                    encoded = ProtobufFormat.Instance.Serialize(operation.ResultType, operation.ReturnType is null ? Array.Empty<object?>() : new[] { result }, chrysalis.Options.Profile);
                }
                catch (SerializationException exception)
                {
                    throw new ChrysalisException(ChrysalisStatus.Internal, exception.Message, exception);
                }
                return [0, .. encoded];
            }
            catch (InvalidDataException exception)
            {
                return Error(ChrysalisStatus.InvalidArgument, exception.Message);
            }
            catch (ChrysalisException exception)
            {
                return Error(exception.Status, exception.Message);
            }
        }

        private static byte[] Error(ChrysalisStatus status, string message) => [(byte)status, .. Encoding.UTF8.GetBytes(message)];
    }
}

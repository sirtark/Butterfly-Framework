using Butterfly.Serialization;
using Butterfly.Serialization.Protobuf;
using Butterfly.Communication;
using System.Collections.Concurrent;
using System.Text;

namespace Butterfly.Chrysalis.Binary
{
    /// <summary>
    /// Client of the Chrysalis binary protocol. It is an <see cref="IChrysalisInvoker"/>: <see cref="CreateClient{TContract}"/>
    /// returns the generated typed client of a service over this connection. Calls are multiplexed and thread-safe.
    /// </summary>
    public sealed class ChrysalisBinaryClient : IChrysalisInvoker, IAsyncDisposable, IDisposable
    {
        private readonly NetworkConnection connection;
        private readonly Lock writeLock = new();
        private readonly ConcurrentDictionary<uint, TaskCompletionSource<(byte Status, byte[] Payload)>> pending = new();
        private readonly Thread reader;
        private readonly Timer keepAlive;
        private readonly int maxFrameSize;
        private uint nextCall;
        private volatile Exception? failure;

        private ChrysalisBinaryClient(NetworkConnection connection, int maxFrameSize)
        {
            this.connection = connection;
            this.maxFrameSize = maxFrameSize;
            reader = new Thread(ReadLoop) { IsBackground = true, Name = "Chrysalis binary client" };
            keepAlive = new Timer(_ => TrySend(BinaryProtocol.Ping, Interlocked.Increment(ref nextCall)));
        }

        /// <summary>Profile used to encode arguments and decode results; it must match the server's (Default by default).</summary>
        public SerializationProfile Profile { get; set; } = SerializationProfile.Default;

        /// <summary>Sent with every call (authorization tokens, tenant...); servers see it in <see cref="ChrysalisCallContext.Headers"/>.</summary>
        public ConcurrentDictionary<string, string> Metadata { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool IsConnected => failure is null && !connection.IsDisposed;

        /// <param name="options">
        /// Connection settings. By default responses are awaited without a read timeout (calls may take long) and the
        /// connection is kept alive with a ping every <paramref name="keepAliveInterval"/>.
        /// </param>
        /// <param name="keepAliveInterval">Ping period that keeps idle connections open (default 1 minute); InfiniteTimeSpan disables it.</param>
        /// <exception cref="ConnectionFailedException">The server could not be reached.</exception>
        /// <exception cref="IOException">The server does not speak the Chrysalis binary protocol.</exception>
        public static async Task<ChrysalisBinaryClient> ConnectAsync(string host, int port, bool useTls = false, ConnectionOptions? options = null,
            int maxFrameSize = 16 * 1024 * 1024, TimeSpan? keepAliveInterval = null, CancellationToken cancellationToken = default)
        {
            options ??= new ConnectionOptions { ReadTimeout = Timeout.InfiniteTimeSpan };
            var connection = await NetworkConnection.ConnectAsync(host, port, options, useTls, cancellationToken: cancellationToken).ConfigureAwait(false);
            try
            {
                await connection.RunAsync(() =>
                {
                    connection.Write(BinaryProtocol.Handshake());
                    connection.Flush();
                    var answer = new byte[5];
                    if (!BinaryProtocol.ReadExactly(connection.Reader.AsStream(), answer, allowEnd: true) || !BinaryProtocol.IsHandshake(answer))
                        throw new IOException("The server did not answer the Chrysalis handshake.");
                }, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                connection.Dispose();
                throw;
            }

            var client = new ChrysalisBinaryClient(connection, maxFrameSize);
            client.reader.Start();
            var interval = keepAliveInterval ?? TimeSpan.FromMinutes(1);
            client.keepAlive.Change(interval, interval);
            return client;
        }

        public TContract CreateClient<TContract>() where TContract : class => ChrysalisRegistry.CreateClient<TContract>(this);

        public async ValueTask<object?> InvokeAsync(ChrysalisOperation operation, object?[] arguments, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(operation);
            ArgumentNullException.ThrowIfNull(arguments);
            if (failure is not null)
                throw Unavailable();

            var payload = new List<byte>(64);
            BinaryProtocol.WriteString(payload, operation.FullName);
            var metadata = Metadata.ToArray();
            payload.Add((byte)(metadata.Length >> 8));
            payload.Add((byte)metadata.Length);
            foreach (var (key, value) in metadata)
            {
                BinaryProtocol.WriteString(payload, key);
                BinaryProtocol.WriteString(payload, value);
            }
            try
            {
                payload.AddRange(ProtobufFormat.Instance.Serialize(operation.ParametersType, arguments, Profile));
            }
            catch (SerializationException exception)
            {
                throw ChrysalisException.InvalidInput(exception);
            }

            var call = Interlocked.Increment(ref nextCall);
            var completion = new TaskCompletionSource<(byte, byte[])>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending[call] = completion;
            try
            {
                Send(BinaryProtocol.Request, call, [.. payload]);
                // Cancelling tells the server to stop working on the call.
                using var registration = cancellationToken.Register(() =>
                {
                    if (completion.TrySetCanceled(cancellationToken))
                        TrySend(BinaryProtocol.Cancel, call);
                });

                var (status, response) = await completion.Task.ConfigureAwait(false);
                if (status != 0)
                    throw new ChrysalisException((ChrysalisStatus)status, Encoding.UTF8.GetString(response));

                object?[] result;
                try
                {
                    result = (object?[])ProtobufFormat.Instance.Deserialize(operation.ResultType, response, Profile)!;
                }
                catch (SerializationException exception)
                {
                    throw new ChrysalisException(ChrysalisStatus.DataLoss, $"The response does not match the contract: {exception.Message}", exception);
                }
                return operation.ReturnType is null ? null : result[0];
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw new ChrysalisException(ChrysalisStatus.Cancelled, "The call was cancelled.");
            }
            finally
            {
                pending.TryRemove(call, out _);
            }
        }

        /// <summary>Checks that the server answers and measures the round trip.</summary>
        public async Task<TimeSpan> PingAsync(CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref nextCall);
            var completion = new TaskCompletionSource<(byte, byte[])>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending[call] = completion;
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                Send(BinaryProtocol.Ping, call, []);
                await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                return System.Diagnostics.Stopwatch.GetElapsedTime(started);
            }
            finally
            {
                pending.TryRemove(call, out _);
            }
        }

        private void Send(byte type, uint call, ReadOnlySpan<byte> payload)
        {
            var frame = BinaryProtocol.Frame(type, call, payload);
            if (frame.Length - 4 > maxFrameSize)
                throw new ChrysalisException(ChrysalisStatus.ResourceExhausted, "The call is larger than the maximum frame size.");
            try
            {
                lock (writeLock)
                {
                    connection.Write(frame);
                    connection.Flush();
                }
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                Fail(exception);
                throw Unavailable();
            }
        }

        private void TrySend(byte type, uint call)
        {
            try
            {
                Send(type, call, []);
            }
            catch (ChrysalisException)
            {
            }
        }

        private void ReadLoop()
        {
            try
            {
                while (BinaryProtocol.ReadFrame(connection.Reader.AsStream(), maxFrameSize) is { } frame)
                {
                    if (frame.Type is not (BinaryProtocol.Response or BinaryProtocol.Pong))
                        continue;
                    if (pending.TryGetValue(frame.Call, out var completion))
                        completion.TrySetResult(frame.Type == BinaryProtocol.Pong ? ((byte)0, []) : (frame.Payload[0], frame.Payload[1..]));
                }
                Fail(new EndOfStreamException("The server closed the connection."));
            }
            catch (Exception exception)
            {
                Fail(exception);
            }
        }

        private void Fail(Exception exception)
        {
            failure ??= exception;
            foreach (var completion in pending.Values)
                completion.TrySetException(Unavailable());
        }

        private ChrysalisException Unavailable() =>
            new(ChrysalisStatus.Unavailable, $"The connection to the server is closed: {failure?.Message}", failure);

        public void Dispose()
        {
            keepAlive.Dispose();
            Fail(new ObjectDisposedException(nameof(ChrysalisBinaryClient)));
            connection.Dispose();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return default;
        }
    }
}

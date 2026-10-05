using Butterfly.Networking.Sockets;

namespace Butterfly.Communication
{
    /// <summary>
    /// A <see cref="Stream"/> over a connected <see cref="TcpSocket"/>, so it can be layered with SslStream,
    /// compression streams and the rest of System.IO. Socket errors surface as <see cref="IOException"/>.
    /// </summary>
    public sealed class SocketStream(TcpSocket socket, bool ownsSocket = true) : Stream
    {
        public TcpSocket Socket { get; } = socket;

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override bool CanTimeout => true;

        public override int ReadTimeout
        {
            get => ToMilliseconds(Socket.ReceiveTimeout);
            set => Socket.ReceiveTimeout = FromMilliseconds(value);
        }

        public override int WriteTimeout
        {
            get => ToMilliseconds(Socket.SendTimeout);
            set => Socket.SendTimeout = FromMilliseconds(value);
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty)
                return 0;

            try
            {
                return Socket.Receive(buffer);
            }
            catch (SocketException ex)
            {
                throw new IOException($"Unable to read from the connection: {ex.Message}", ex);
            }
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            try
            {
                while (!buffer.IsEmpty)
                    buffer = buffer[Socket.Send(buffer)..];
            }
            catch (SocketException ex)
            {
                throw new IOException($"Unable to write to the connection: {ex.Message}", ex);
            }
        }

        // Blocking I/O: the async overloads run the synchronous call on the thread pool.
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Task.Run(() => Read(buffer, offset, count), cancellationToken);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => new(Task.Run(() => Read(buffer.Span), cancellationToken));

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Task.Run(() => Write(buffer, offset, count), cancellationToken);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => new(Task.Run(() => Write(buffer.Span), cancellationToken));

        public override void Flush() { }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && ownsSocket)
                Socket.Dispose();

            base.Dispose(disposing);
        }

        private static int ToMilliseconds(TimeSpan timeout)
            => timeout == Timeout.InfiniteTimeSpan ? Timeout.Infinite : (int)timeout.TotalMilliseconds;

        private static TimeSpan FromMilliseconds(int milliseconds)
            => milliseconds is Timeout.Infinite or 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds(milliseconds);
    }
}

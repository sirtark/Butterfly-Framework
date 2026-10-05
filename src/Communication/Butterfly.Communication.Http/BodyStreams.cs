using System.Globalization;
using System.Text;

namespace Butterfly.Communication.Http
{
    /// <summary>Read-only stream base for the framing streams below.</summary>
    internal abstract class ReadOnlyBodyStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override void Flush() { }
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        /// <summary>True once the whole body was received, so the connection can carry another request.</summary>
        public abstract bool IsComplete { get; }
    }

    /// <summary>A body delimited by Content-Length.</summary>
    internal sealed class ContentLengthStream(ProtocolReader reader, long length) : ReadOnlyBodyStream
    {
        private long _remaining = length;

        public override bool IsComplete => _remaining == 0;

        public override int Read(Span<byte> buffer)
        {
            if (_remaining == 0 || buffer.IsEmpty)
                return 0;

            int read = reader.Read(buffer[..(int)Math.Min(buffer.Length, _remaining)]);
            if (read == 0)
                throw new EndOfStreamException($"The connection closed with {_remaining} bytes of the body still missing.");

            _remaining -= read;
            return read;
        }
    }

    /// <summary>Transfer-Encoding: chunked (RFC 9112 7.1). Trailer fields are read and discarded.</summary>
    internal sealed class ChunkedStream(ProtocolReader reader) : ReadOnlyBodyStream
    {
        private long _chunkRemaining;
        private bool _done;

        public override bool IsComplete => _done;

        public override int Read(Span<byte> buffer)
        {
            if (_done || buffer.IsEmpty)
                return 0;

            if (_chunkRemaining == 0)
            {
                _chunkRemaining = ReadChunkSize();
                if (_chunkRemaining == 0)
                {
                    // Trailer section, ended by an empty line.
                    while (!string.IsNullOrEmpty(reader.ReadLine(Encoding.Latin1))) { }
                    _done = true;
                    return 0;
                }
            }

            int read = reader.Read(buffer[..(int)Math.Min(buffer.Length, _chunkRemaining)]);
            if (read == 0)
                throw new EndOfStreamException("The connection closed in the middle of a chunk.");

            _chunkRemaining -= read;
            if (_chunkRemaining == 0 && reader.ReadLine() is not "")
                throw new HttpException("A chunk was not followed by CRLF.");

            return read;
        }

        private long ReadChunkSize()
        {
            string line = reader.ReadLine(Encoding.Latin1) ?? throw new EndOfStreamException("The connection closed before the next chunk.");
            int extension = line.IndexOf(';');
            string size = (extension >= 0 ? line[..extension] : line).Trim();

            if (!long.TryParse(size, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out long length) || length < 0)
                throw new HttpException($"Invalid chunk size '{size}'.");

            return length;
        }
    }

    /// <summary>A body that ends when the server closes the connection (HTTP/1.0 style).</summary>
    internal sealed class UntilCloseStream(ProtocolReader reader) : ReadOnlyBodyStream
    {
        private bool _done;

        // The connection is closed afterwards, so it is never reusable.
        public override bool IsComplete => false;

        public override int Read(Span<byte> buffer)
        {
            if (_done)
                return 0;

            int read = reader.Read(buffer);
            _done = read == 0;
            return read;
        }
    }

    internal sealed class EmptyBodyStream : ReadOnlyBodyStream
    {
        public override bool IsComplete => true;
        public override int Read(Span<byte> buffer) => 0;
    }

    /// <summary>
    /// Writes a body with chunked transfer encoding (used when the length is unknown). Disposing writes the last chunk.
    /// </summary>
    internal sealed class ChunkedWriteStream(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (buffer.IsEmpty)
                return;

            inner.Write(Encoding.ASCII.GetBytes(buffer.Length.ToString("X", CultureInfo.InvariantCulture) + "\r\n"));
            inner.Write(buffer);
            inner.Write("\r\n"u8);
        }

        public void Complete() => inner.Write("0\r\n\r\n"u8);

        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>
    /// What the caller reads: the decoded body on top of the framing stream. When the body ends the connection goes
    /// back to the pool; disposing it early closes the connection instead (the rest of the body is still in flight).
    /// </summary>
    internal sealed class ResponseBodyStream(Stream decoded, ReadOnlyBodyStream framing, Action<bool> release) : Stream
    {
        private int _released;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (Volatile.Read(ref _released) != 0)
                return 0;

            int read;
            try
            {
                read = decoded.Read(buffer);
            }
            catch
            {
                Release(reusable: false);
                throw;
            }

            if (read == 0 && !buffer.IsEmpty)
            {
                // Decompressors stop at their own trailer: let the framing reach its end (last chunk, trailers).
                Span<byte> scratch = stackalloc byte[256];
                try
                {
                    for (int i = 0; i < 16 && !framing.IsComplete; i++)
                    {
                        if (framing.Read(scratch) == 0)
                            break;
                    }
                }
                catch (IOException)
                {
                }

                Release(framing.IsComplete);
            }

            return read;
        }

        protected override void Dispose(bool disposing)
        {
            // Disposed before the end: the connection is reusable only if the framing happens to be complete.
            if (disposing)
                Release(framing.IsComplete);
            base.Dispose(disposing);
        }

        private void Release(bool reusable)
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                release(reusable);
        }

        public override void Flush() { }
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

using System.Text;

namespace Butterfly.Communication
{
    /// <summary>
    /// Buffered reader for protocols that mix CRLF-terminated lines with raw bytes (SMTP, POP3, IMAP, FTP,
    /// HTTP headers and bodies). Lines and bytes can be interleaved freely.
    /// </summary>
    public sealed class ProtocolReader
    {
        private readonly byte[] _buffer;
        private readonly int _maxLineLength;
        private int _start;
        private int _end;

        public ProtocolReader(Stream stream, int bufferSize = 16 * 1024, int maxLineLength = 1024 * 1024)
        {
            ArgumentNullException.ThrowIfNull(stream);
            ArgumentOutOfRangeException.ThrowIfLessThan(bufferSize, 16);
            BaseStream = stream;
            _buffer = new byte[bufferSize];
            _maxLineLength = maxLineLength;
        }

        public Stream BaseStream { get; }

        /// <summary>Bytes already received but not yet consumed.</summary>
        public int BufferedCount => _end - _start;

        /// <summary>Reads a line without its terminator (CRLF or a bare LF). Returns null at the end of the stream.</summary>
        /// <exception cref="ProtocolViolationException">The line is longer than the configured maximum.</exception>
        public string? ReadLine(Encoding? encoding = null)
        {
            byte[]? line = ReadLineBytes();
            return line is null ? null : (encoding ?? Encoding.UTF8).GetString(line);
        }

        /// <inheritdoc cref="ReadLine"/>
        public byte[]? ReadLineBytes()
        {
            MemoryStream? overflow = null;

            while (true)
            {
                int newLine = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
                if (newLine >= 0)
                {
                    int length = newLine - _start;
                    if (length > 0 && _buffer[newLine - 1] == '\r')
                        length--;
                    else if (length == 0 && overflow is { Length: > 0 } && overflow.GetBuffer()[overflow.Length - 1] == '\r')
                        overflow.SetLength(overflow.Length - 1);

                    byte[] line;
                    if (overflow is null)
                    {
                        line = _buffer.AsSpan(_start, length).ToArray();
                    }
                    else
                    {
                        overflow.Write(_buffer, _start, length);
                        line = overflow.ToArray();
                    }

                    _start = newLine + 1;
                    return line;
                }

                // No terminator yet: keep what we have and read more.
                if (_end > _start)
                {
                    overflow ??= new MemoryStream();
                    overflow.Write(_buffer, _start, _end - _start);
                    if (overflow.Length > _maxLineLength)
                        throw new ProtocolViolationException($"The server sent a line longer than {_maxLineLength} bytes.");
                    _start = _end;
                }

                if (!Fill())
                {
                    if (overflow is null || overflow.Length == 0)
                        return null;

                    // The stream ended in the middle of a line: hand back what arrived.
                    return overflow.ToArray();
                }
            }
        }

        /// <summary>Reads up to <paramref name="destination"/>.Length bytes; 0 means the end of the stream.</summary>
        public int Read(Span<byte> destination)
        {
            if (destination.IsEmpty)
                return 0;

            if (_end == _start)
            {
                // Large reads bypass the buffer.
                if (destination.Length >= _buffer.Length)
                    return BaseStream.Read(destination);

                if (!Fill())
                    return 0;
            }

            int count = Math.Min(destination.Length, _end - _start);
            _buffer.AsSpan(_start, count).CopyTo(destination);
            _start += count;
            return count;
        }

        /// <exception cref="EndOfStreamException">The stream ended first.</exception>
        public void ReadExactly(Span<byte> destination)
        {
            while (!destination.IsEmpty)
            {
                int read = Read(destination);
                if (read == 0)
                    throw new EndOfStreamException("The connection was closed before all the expected data arrived.");
                destination = destination[read..];
            }
        }

        public byte[] ReadBytes(int count)
        {
            byte[] bytes = new byte[count];
            ReadExactly(bytes);
            return bytes;
        }

        /// <summary>Copies exactly <paramref name="count"/> bytes to <paramref name="destination"/>.</summary>
        public void CopyTo(Stream destination, long count)
        {
            byte[] chunk = new byte[Math.Min(count, 81920)];
            while (count > 0)
            {
                int read = Read(chunk.AsSpan(0, (int)Math.Min(chunk.Length, count)));
                if (read == 0)
                    throw new EndOfStreamException("The connection was closed before all the expected data arrived.");
                destination.Write(chunk, 0, read);
                count -= read;
            }
        }

        /// <summary>A read-only stream over this reader (buffered bytes first). Disposing it does not close anything.</summary>
        public Stream AsStream() => new ReaderStream(this);

        private bool Fill()
        {
            if (_start == _end)
                _start = _end = 0;
            else if (_end == _buffer.Length)
            {
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }

            int read = BaseStream.Read(_buffer, _end, _buffer.Length - _end);
            _end += read;
            return read > 0;
        }

        private sealed class ReaderStream(ProtocolReader reader) : Stream
        {
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;

            public override int Read(byte[] buffer, int offset, int count) => reader.Read(buffer.AsSpan(offset, count));
            public override int Read(Span<byte> buffer) => reader.Read(buffer);

            public override void Flush() { }
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}

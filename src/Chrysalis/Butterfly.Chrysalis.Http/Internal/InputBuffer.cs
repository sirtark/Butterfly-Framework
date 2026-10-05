using System.Text;

namespace Butterfly.Chrysalis.Http.Internal
{
    /// <summary>A malformed or oversized HTTP/1.x request: answered with <see cref="Status"/>, then the connection is closed.</summary>
    internal sealed class HttpProtocolException(int status, string message) : Exception(message)
    {
        public int Status { get; } = status;
    }

    /// <summary>Buffered reads from a connection: lines for HTTP/1.x heads, exact byte counts for bodies and HTTP/2 frames.</summary>
    internal sealed class InputBuffer(Stream stream)
    {
        private readonly byte[] buffer = new byte[16 * 1024];
        private int start;
        private int end;

        public Stream Stream { get; } = stream;

        public int Buffered => end - start;

        /// <summary>Reads a line ending in LF (a preceding CR is removed), decoded as Latin-1.</summary>
        /// <returns>The line, or null when the connection ended cleanly before any byte of it.</returns>
        /// <exception cref="HttpProtocolException">The line is longer than <paramref name="maxLength"/> (<paramref name="tooLongStatus"/>).</exception>
        public string? ReadLine(int maxLength, int tooLongStatus)
        {
            var line = new List<byte>(128);
            while (true)
            {
                if (start == end && Fill() == 0)
                {
                    if (line.Count == 0)
                        return null;
                    throw new IOException("The connection closed in the middle of a line.");
                }

                var span = buffer.AsSpan(start, end - start);
                var newline = span.IndexOf((byte)'\n');
                var take = newline < 0 ? span.Length : newline;
                if (line.Count + take > maxLength)
                    throw new HttpProtocolException(tooLongStatus, "The request line or a header is too long.");

                line.AddRange(span[..take].ToArray());
                start += newline < 0 ? take : take + 1;
                if (newline >= 0)
                {
                    if (line.Count > 0 && line[^1] == '\r')
                        line.RemoveAt(line.Count - 1);
                    return Encoding.Latin1.GetString(line.ToArray());
                }
            }
        }

        /// <exception cref="EndOfStreamException">The connection ended first.</exception>
        public void ReadExactly(Span<byte> destination)
        {
            while (!destination.IsEmpty)
            {
                if (start == end && Fill() == 0)
                    throw new EndOfStreamException("The connection closed unexpectedly.");

                var count = Math.Min(destination.Length, end - start);
                buffer.AsSpan(start, count).CopyTo(destination);
                start += count;
                destination = destination[count..];
            }
        }

        private int Fill()
        {
            start = end = 0;
            var read = Stream.Read(buffer, 0, buffer.Length);
            end = read;
            return read;
        }
    }
}

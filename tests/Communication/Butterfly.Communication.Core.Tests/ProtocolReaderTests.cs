using System.Text;

namespace Butterfly.Communication.Tests
{
    public class ProtocolReaderTests
    {
        [Fact]
        public void ReadsCrLfAndBareLfLines()
        {
            var reader = new ProtocolReader(new MemoryStream("uno\r\ndos\ntres\r\n\r\n"u8.ToArray()));

            Assert.Equal("uno", reader.ReadLine());
            Assert.Equal("dos", reader.ReadLine());
            Assert.Equal("tres", reader.ReadLine());
            Assert.Equal("", reader.ReadLine());
            Assert.Null(reader.ReadLine());
        }

        [Fact]
        public void HandlesLinesSplitAcrossReads()
        {
            // One byte per read: CR and LF always land in different reads.
            var reader = new ProtocolReader(new TrickleStream("hola butterfly\r\nsegunda\r\n"u8.ToArray()), bufferSize: 16);

            Assert.Equal("hola butterfly", reader.ReadLine());
            Assert.Equal("segunda", reader.ReadLine());
        }

        [Fact]
        public void MixesLinesAndBytes()
        {
            var reader = new ProtocolReader(new MemoryStream("HEADER\r\n12345rest\r\n"u8.ToArray()));

            Assert.Equal("HEADER", reader.ReadLine());
            Assert.Equal("12345"u8.ToArray(), reader.ReadBytes(5));
            Assert.Equal("rest", reader.ReadLine());
        }

        [Fact]
        public void ReturnsPartialLineAtEndOfStream()
        {
            var reader = new ProtocolReader(new MemoryStream("sin fin"u8.ToArray()));
            Assert.Equal("sin fin", reader.ReadLine());
            Assert.Null(reader.ReadLine());
        }

        [Fact]
        public void RejectsLinesOverTheLimit()
        {
            var reader = new ProtocolReader(new MemoryStream(Encoding.ASCII.GetBytes(new string('x', 5000) + "\r\n")), bufferSize: 64, maxLineLength: 1000);
            Assert.Throws<ProtocolViolationException>(() => reader.ReadLine());
        }

        [Fact]
        public void ReadExactlyFailsOnShortStream()
        {
            var reader = new ProtocolReader(new MemoryStream([1, 2, 3]));
            Assert.Throws<EndOfStreamException>(() => reader.ReadBytes(4));
        }

        private sealed class TrickleStream(byte[] data) : MemoryStream(data)
        {
            public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, 1));
            public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(buffer.Length, 1)]);
        }
    }
}

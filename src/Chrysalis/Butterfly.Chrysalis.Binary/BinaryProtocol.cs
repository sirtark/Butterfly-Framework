using System.Buffers.Binary;
using System.Text;

namespace Butterfly.Chrysalis.Binary
{
    /// <summary>
    /// The Chrysalis binary protocol. After a handshake ("CHRY" + version, both ways) the connection carries frames:
    /// <code>
    /// frame    = length:u32 type:u8 call:u32 payload      (big endian; length counts type, call and payload)
    /// Request  = name:str16 metadata-count:u16 (key:str16 value:str16)* parameters:protobuf
    /// Response = status:u8 (status = 0: result:protobuf | otherwise: message:utf8)
    /// Cancel, Ping, Pong = no payload
    /// </code>
    /// Calls are identified by the client and answered in any order, so one connection carries many concurrent calls.
    /// </summary>
    internal static class BinaryProtocol
    {
        public static ReadOnlySpan<byte> Magic => "CHRY"u8;
        public const byte Version = 1;

        public const byte Request = 1, Response = 2, Cancel = 3, Ping = 4, Pong = 5;

        public static byte[] Handshake() => [.. Magic, Version];

        public static bool IsHandshake(ReadOnlySpan<byte> bytes) => bytes.Length == 5 && bytes[..4].SequenceEqual(Magic) && bytes[4] == Version;

        public static byte[] Frame(byte type, uint call, ReadOnlySpan<byte> payload)
        {
            var frame = new byte[9 + payload.Length];
            BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)(5 + payload.Length));
            frame[4] = type;
            BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(5), call);
            payload.CopyTo(frame.AsSpan(9));
            return frame;
        }

        /// <returns>The frame, or null when the connection ended cleanly between frames.</returns>
        public static (byte Type, uint Call, byte[] Payload)? ReadFrame(Stream stream, int maxFrameSize)
        {
            Span<byte> header = stackalloc byte[4];
            if (!ReadExactly(stream, header, allowEnd: true))
                return null;
            var length = BinaryPrimitives.ReadUInt32BigEndian(header);
            if (length < 5 || length > maxFrameSize)
                throw new InvalidDataException($"Invalid frame length {length}.");

            var body = new byte[length];
            ReadExactly(stream, body, allowEnd: false);
            return (body[0], BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(1)), body[5..]);
        }

        public static bool ReadExactly(Stream stream, Span<byte> buffer, bool allowEnd)
        {
            var total = 0;
            while (total < buffer.Length)
            {
                var read = stream.Read(buffer[total..]);
                if (read == 0)
                {
                    if (allowEnd && total == 0)
                        return false;
                    throw new EndOfStreamException("The connection closed in the middle of a frame.");
                }
                total += read;
            }
            return true;
        }

        public static void WriteString(List<byte> output, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            if (bytes.Length > ushort.MaxValue)
                throw new ArgumentException("A string is longer than 65535 bytes.", nameof(value));
            output.Add((byte)(bytes.Length >> 8));
            output.Add((byte)bytes.Length);
            output.AddRange(bytes);
        }

        public static string ReadString(ReadOnlySpan<byte> payload, ref int position)
        {
            if (payload.Length - position < 2)
                throw new InvalidDataException("Truncated string.");
            var length = BinaryPrimitives.ReadUInt16BigEndian(payload[position..]);
            position += 2;
            if (payload.Length - position < length)
                throw new InvalidDataException("Truncated string.");
            var value = Encoding.UTF8.GetString(payload.Slice(position, length));
            position += length;
            return value;
        }

        public static ushort ReadUInt16(ReadOnlySpan<byte> payload, ref int position)
        {
            if (payload.Length - position < 2)
                throw new InvalidDataException("Truncated frame.");
            var value = BinaryPrimitives.ReadUInt16BigEndian(payload[position..]);
            position += 2;
            return value;
        }
    }
}

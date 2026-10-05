using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Butterfly.Networking.Sockets
{
    internal static class IPAddressText
    {
        public static bool TryParseIPv4(ReadOnlySpan<char> text, Span<byte> destination)
        {
            for (int i = 0; i < 4; i++)
            {
                bool last = i == 3;
                int dot = text.IndexOf('.');

                if (last != (dot < 0))
                    return false;

                var part = last ? text : text[..dot];

                // Leading zeros are rejected because some parsers read them as octal.
                if (part.Length is 0 or > 3 || (part.Length > 1 && part[0] == '0'))
                    return false;

                if (!byte.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out destination[i]))
                    return false;

                if (!last)
                    text = text[(dot + 1)..];
            }

            return true;
        }

        public static bool TryParseIPv6(ReadOnlySpan<char> text, Span<byte> destination, out uint scopeId)
        {
            scopeId = 0;

            int percent = text.IndexOf('%');
            if (percent >= 0)
            {
                if (!uint.TryParse(text[(percent + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out scopeId))
                    return false;

                text = text[..percent];
            }

            Span<ushort> groups = stackalloc ushort[8];
            Span<byte> ipv4 = stackalloc byte[4];
            int count = 0;
            int compressAt = -1;

            if (text.StartsWith("::"))
            {
                compressAt = 0;
                text = text[2..];
            }
            else if (text.IsEmpty)
            {
                return false;
            }

            while (!text.IsEmpty)
            {
                int colon = text.IndexOf(':');
                var part = colon < 0 ? text : text[..colon];

                if (colon < 0 && part.Contains('.'))
                {
                    if (count > 6 || !TryParseIPv4(part, ipv4))
                        return false;

                    groups[count++] = (ushort)(ipv4[0] << 8 | ipv4[1]);
                    groups[count++] = (ushort)(ipv4[2] << 8 | ipv4[3]);
                    break;
                }

                if (part.Length is 0 or > 4 || count == 8)
                    return false;

                if (!ushort.TryParse(part, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out groups[count]))
                    return false;

                count++;

                if (colon < 0)
                    break;

                text = text[(colon + 1)..];

                if (text.StartsWith(':'))
                {
                    if (compressAt >= 0)
                        return false;

                    compressAt = count;
                    text = text[1..];
                }
                else if (text.IsEmpty)
                {
                    return false;
                }
            }

            if (compressAt < 0 ? count != 8 : count > 7)
                return false;

            destination[..16].Clear();

            int tail = compressAt < 0 ? count : count - compressAt;
            int head = count - tail;

            for (int i = 0; i < head; i++)
                BinaryPrimitives.WriteUInt16BigEndian(destination[(i * 2)..], groups[i]);

            for (int i = 0; i < tail; i++)
                BinaryPrimitives.WriteUInt16BigEndian(destination[((8 - tail + i) * 2)..], groups[head + i]);

            return true;
        }

        public static string FormatIPv4(ReadOnlySpan<byte> address)
            => string.Create(CultureInfo.InvariantCulture, $"{address[0]}.{address[1]}.{address[2]}.{address[3]}");

        public static string FormatIPv6(ReadOnlySpan<byte> address, uint scopeId)
        {
            Span<ushort> groups = stackalloc ushort[8];
            for (int i = 0; i < 8; i++)
                groups[i] = BinaryPrimitives.ReadUInt16BigEndian(address[(i * 2)..]);

            // RFC 5952: compress the longest run of two or more zero groups, the first one on ties.
            int bestStart = -1;
            int bestLength = 1;
            for (int i = 0; i < 8;)
            {
                if (groups[i] != 0)
                {
                    i++;
                    continue;
                }

                int start = i;
                while (i < 8 && groups[i] == 0)
                    i++;

                if (i - start > bestLength)
                {
                    bestStart = start;
                    bestLength = i - start;
                }
            }

            bool ipv4Mapped = bestStart == 0 && bestLength == 5 && groups[5] == 0xFFFF;

            var builder = new StringBuilder(64);
            for (int i = 0; i < 8; i++)
            {
                if (i == bestStart)
                {
                    builder.Append("::");
                    i += bestLength - 1;
                    continue;
                }

                if (i > 0 && builder[^1] != ':')
                    builder.Append(':');

                if (ipv4Mapped && i == 6)
                {
                    builder.Append(FormatIPv4(address[12..]));
                    break;
                }

                builder.Append(groups[i].ToString("x", CultureInfo.InvariantCulture));
            }

            if (scopeId != 0)
                builder.Append('%').Append(scopeId.ToString(CultureInfo.InvariantCulture));

            return builder.ToString();
        }
    }
}

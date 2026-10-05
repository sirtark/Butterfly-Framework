using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

using Butterfly.Networking.Sockets;

namespace Butterfly.Communication.Ntp
{
    public enum NtpLeapIndicator
    {
        NoWarning = 0,
        LastMinuteHas61Seconds = 1,
        LastMinuteHas59Seconds = 2,
        Unsynchronized = 3
    }

    public sealed record NtpResult(
        DateTimeOffset ServerTime,
        TimeSpan Offset,
        TimeSpan RoundTripDelay,
        int Stratum,
        string ReferenceId,
        int Version,
        NtpLeapIndicator LeapIndicator,
        SocketAddress Server)
    {
        /// <summary>The current time corrected with the measured offset.</summary>
        public DateTimeOffset CorrectedUtcNow => DateTimeOffset.UtcNow + Offset;
    }

    public class NtpException : CommunicationException
    {
        public NtpException(string message, Exception? innerException = null) : base(message, innerException) { }

        /// <summary>The Kiss-o'-Death code (RATE, DENY, RSTR...) when the server asked us to back off.</summary>
        public string? KissCode { get; init; }
    }

    public sealed class NtpClient(ConnectionOptions? options = null)
    {
        public const string DefaultServer = "pool.ntp.org";
        public const ushort Port = 123;

        private static readonly DateTime s_era0 = new(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private readonly ConnectionOptions _options = options ?? ConnectionOptions.Default;

        /// <summary>How long to wait for each server address.</summary>
        public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(3);

        /// <summary>Asks <paramref name="server"/> for the time, trying each of its addresses until one answers.</summary>
        public NtpResult Query(string server = DefaultServer, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<SocketAddress> addresses = (_options.Resolver ?? HostResolver.Default).Resolve(server, cancellationToken);
            Exception? lastError = null;

            foreach (SocketAddress address in addresses)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    return Query(new SocketAddress(address.GetAddressBytes(), Port, address.ScopeId));
                }
                catch (NtpException ex) when (ex.KissCode is null)
                {
                    lastError = ex;
                }
                catch (SocketException ex)
                {
                    lastError = ex;
                }
            }

            throw new NtpException($"No address of '{server}' answered.", lastError);
        }

        public Task<NtpResult> QueryAsync(string server = DefaultServer, CancellationToken cancellationToken = default)
            => Task.Run(() => Query(server, cancellationToken), cancellationToken);

        /// <summary>Asks one server address (port included) for the time.</summary>
        public NtpResult Query(SocketAddress server)
        {
            using var socket = new UdpSocket(server.Family) { ReceiveTimeout = Timeout };

            // LI = 0, VN = 4, Mode = 3 (client). The transmit timestamp is random: the server must echo it back
            // as the originate timestamp, which proves the answer is for this request (RFC 5905 anti-spoofing).
            byte[] request = new byte[48];
            request[0] = 0b00_100_011;
            byte[] nonce = RandomNumberGenerator.GetBytes(8);
            nonce.CopyTo(request, 40);

            DateTimeOffset sent = DateTimeOffset.UtcNow;
            var clock = Stopwatch.StartNew();
            socket.SendTo(request, server);

            byte[] response = new byte[512];
            while (true)
            {
                int length = socket.ReceiveFrom(response, out SocketAddress from);
                TimeSpan elapsed = clock.Elapsed;

                if (length < 48 || !from.Equals(server) || !response.AsSpan(24, 8).SequenceEqual(nonce))
                {
                    if (elapsed > Timeout)
                        throw new NtpException("The server did not answer in time.");
                    continue;
                }

                return Interpret(response.AsSpan(0, length), server, sent, sent + elapsed, elapsed);
            }
        }

        private static NtpResult Interpret(ReadOnlySpan<byte> packet, SocketAddress server, DateTimeOffset t1, DateTimeOffset t4, TimeSpan localElapsed)
        {
            var leap = (NtpLeapIndicator)(packet[0] >> 6);
            int version = (packet[0] >> 3) & 0x7;
            int mode = packet[0] & 0x7;
            int stratum = packet[1];
            ReadOnlySpan<byte> referenceId = packet.Slice(12, 4);

            if (mode != 4)
                throw new NtpException($"The reply is not from a server (mode {mode}).");

            // Stratum 0 is a Kiss-o'-Death: the reference id carries an ASCII code telling us to stop or slow down.
            if (stratum == 0)
            {
                string code = Encoding.ASCII.GetString(referenceId).TrimEnd('\0');
                throw new NtpException($"The server refused the request (Kiss-o'-Death '{code}').") { KissCode = code };
            }

            if (leap == NtpLeapIndicator.Unsynchronized)
                throw new NtpException("The server clock is not synchronized.");

            DateTimeOffset t2 = ReadTimestamp(packet[32..]);
            DateTimeOffset t3 = ReadTimestamp(packet[40..]);

            // Offset = ((T2 - T1) + (T3 - T4)) / 2 ; Delay = (T4 - T1) - (T3 - T2)
            TimeSpan offset = ((t2 - t1) + (t3 - t4)) / 2;
            TimeSpan delay = localElapsed - (t3 - t2);

            string reference = stratum == 1
                ? Encoding.ASCII.GetString(referenceId).TrimEnd('\0')
                : new SocketAddress(referenceId, 0).AddressToString();

            return new NtpResult(t3, offset, delay < TimeSpan.Zero ? TimeSpan.Zero : delay, stratum, reference, version, leap, server);
        }

        /// <summary>64-bit NTP timestamp: seconds since 1900 and a binary fraction. Handles the 2036 era rollover.</summary>
        internal static DateTimeOffset ReadTimestamp(ReadOnlySpan<byte> data)
        {
            ulong seconds = BinaryPrimitives.ReadUInt32BigEndian(data);
            ulong fraction = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);

            // Values with the top bit clear belong to era 1 (after 7 Feb 2036).
            if ((seconds & 0x80000000) == 0)
                seconds += 1UL << 32;

            long ticks = (long)(seconds * TimeSpan.TicksPerSecond) + (long)(fraction * TimeSpan.TicksPerSecond >> 32);
            return new DateTimeOffset(s_era0.AddTicks(ticks));
        }

        internal static void WriteTimestamp(Span<byte> destination, DateTimeOffset time)
        {
            long ticks = (time.UtcDateTime - s_era0).Ticks;
            ulong seconds = (ulong)(ticks / TimeSpan.TicksPerSecond);
            ulong fraction = (ulong)(ticks % TimeSpan.TicksPerSecond << 32) / TimeSpan.TicksPerSecond;
            BinaryPrimitives.WriteUInt32BigEndian(destination, (uint)seconds);
            BinaryPrimitives.WriteUInt32BigEndian(destination[4..], (uint)fraction);
        }
    }
}

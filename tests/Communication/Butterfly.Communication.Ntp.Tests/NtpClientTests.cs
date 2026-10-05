using System.Text;

using Butterfly.Communication.Testing;

namespace Butterfly.Communication.Ntp.Tests
{
    public class NtpClientTests
    {
        private static readonly NtpClient Client = new() { Timeout = TimeSpan.FromMilliseconds(500) };

        [Fact]
        public void MeasuresTheClockOffset()
        {
            // A server whose clock runs 90 seconds ahead.
            TimeSpan skew = TimeSpan.FromSeconds(90);
            using var server = new UdpTestServer(request => Reply(request, DateTimeOffset.UtcNow + skew, stratum: 2, reference: [192, 168, 1, 1]));

            NtpResult result = Client.Query(server.Address);

            Assert.InRange(result.Offset, skew - TimeSpan.FromSeconds(1), skew + TimeSpan.FromSeconds(1));
            Assert.InRange(result.RoundTripDelay, TimeSpan.Zero, TimeSpan.FromSeconds(1));
            Assert.Equal(2, result.Stratum);
            Assert.Equal("192.168.1.1", result.ReferenceId);
            Assert.Equal(4, result.Version);
        }

        [Fact]
        public void ReportsKissOfDeath()
        {
            using var server = new UdpTestServer(request => Reply(request, DateTimeOffset.UtcNow, stratum: 0, reference: "RATE"u8.ToArray()));

            var exception = Assert.Throws<NtpException>(() => Client.Query(server.Address));
            Assert.Equal("RATE", exception.KissCode);
        }

        [Fact]
        public void IgnoresRepliesThatDoNotEchoTheRequest()
        {
            using var server = new UdpTestServer(request =>
            {
                byte[] reply = Reply(request, DateTimeOffset.UtcNow, stratum: 1, reference: "GPS\0"u8.ToArray());
                reply[24] ^= 0xFF; // forged originate timestamp
                return reply;
            });

            Assert.ThrowsAny<Exception>(() => Client.Query(server.Address));
        }

        [Fact]
        public void RejectsUnsynchronizedServers()
        {
            using var server = new UdpTestServer(request =>
            {
                byte[] reply = Reply(request, DateTimeOffset.UtcNow, stratum: 3, reference: [1, 2, 3, 4]);
                reply[0] |= 0b11_000_000;
                return reply;
            });

            Assert.Throws<NtpException>(() => Client.Query(server.Address));
        }

        [Fact]
        public void TimestampsSurviveThe2036Rollover()
        {
            byte[] buffer = new byte[8];
            var after = new DateTimeOffset(2040, 1, 1, 0, 0, 0, TimeSpan.Zero);
            NtpClient.WriteTimestamp(buffer, after);
            Assert.Equal(after, NtpClient.ReadTimestamp(buffer));

            var before = new DateTimeOffset(2026, 9, 30, 12, 34, 56, 789, TimeSpan.Zero);
            NtpClient.WriteTimestamp(buffer, before);
            Assert.InRange(NtpClient.ReadTimestamp(buffer) - before, TimeSpan.FromTicks(-10), TimeSpan.FromTicks(10));
        }

        private static byte[] Reply(byte[] request, DateTimeOffset serverTime, byte stratum, byte[] reference)
        {
            byte[] reply = new byte[48];
            reply[0] = 0b00_100_100; // LI 0, VN 4, mode 4 (server)
            reply[1] = stratum;
            reference.CopyTo(reply, 12);
            request.AsSpan(40, 8).CopyTo(reply.AsSpan(24));          // originate = client's transmit
            NtpClient.WriteTimestamp(reply.AsSpan(32), serverTime);   // receive
            NtpClient.WriteTimestamp(reply.AsSpan(40), serverTime);   // transmit
            return reply;
        }
    }
}

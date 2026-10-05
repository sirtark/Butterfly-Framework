using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

using Butterfly.Communication.Testing;

namespace Butterfly.Communication.WebSockets.Tests
{
    public class WebSocketClientTests
    {
        private static readonly WebSocketClientOptions NoKeepAlive = new() { KeepAliveInterval = null };

        [Fact]
        public void EchoesTextAndBinaryMessages()
        {
            using var server = new TestServer(session =>
            {
                Handshake(session);
                for (int i = 0; i < 3; i++)
                {
                    var (opcode, payload, _) = ReadClientFrame(session);
                    WriteServerFrame(session, opcode, payload);
                }
            });

            using var socket = WebSocketClient.Connect($"ws://localhost:{server.Port}/chat", NoKeepAlive);
            socket.SendText("hola ñandú");
            Assert.Equal("hola ñandú", socket.Receive()!.Text);

            socket.SendBinary([1, 2, 3]);
            WebSocketMessage binary = socket.Receive()!;
            Assert.Equal(WebSocketMessageType.Binary, binary.Type);
            Assert.Equal([1, 2, 3], binary.Data);

            // 70 000 bytes needs the 64-bit length form.
            byte[] big = RandomNumberGenerator.GetBytes(70_000);
            socket.SendBinary(big);
            Assert.Equal(big, socket.Receive()!.Data);
            server.Wait();
        }

        [Fact]
        public void AssemblesFragmentsAndAnswersPings()
        {
            byte[]? pong = null;
            using var server = new TestServer(session =>
            {
                Handshake(session);
                WriteServerFrame(session, 0x1, "frag"u8.ToArray(), fin: false);
                WriteServerFrame(session, 0x9, "latido"u8.ToArray());        // ping between fragments
                WriteServerFrame(session, 0x0, "mentado"u8.ToArray(), fin: true);
                var (opcode, payload, _) = ReadClientFrame(session);
                if (opcode == 0xA)
                    pong = payload;
            });

            using var socket = WebSocketClient.Connect($"ws://127.0.0.1:{server.Port}/", NoKeepAlive);
            Assert.Equal("fragmentado", socket.Receive()!.Text);
            server.Wait();

            Assert.Equal("latido"u8.ToArray(), pong);
        }

        [Fact]
        public void HandlesServerInitiatedClose()
        {
            ushort echoed = 0;
            using var server = new TestServer(session =>
            {
                Handshake(session);
                WriteServerFrame(session, 0x8, [0x03, 0xE9, .. "adiós"u8]); // 1001 going away
                var (opcode, payload, _) = ReadClientFrame(session);
                if (opcode == 0x8)
                    echoed = BinaryPrimitives.ReadUInt16BigEndian(payload);
            });

            using var socket = WebSocketClient.Connect($"ws://127.0.0.1:{server.Port}/", NoKeepAlive);
            Assert.Null(socket.Receive());
            server.Wait();

            Assert.Equal(WebSocketState.Closed, socket.State);
            Assert.Equal(WebSocketCloseStatus.GoingAway, socket.CloseStatus);
            Assert.Equal("adiós", socket.CloseDescription);
            Assert.Equal(1001, echoed);
        }

        [Fact]
        public void ClientCloseCompletesTheHandshake()
        {
            string? reason = null;
            using var server = new TestServer(session =>
            {
                Handshake(session);
                WriteServerFrame(session, 0x1, "pendiente"u8.ToArray());
                var (_, payload, _) = ReadClientFrame(session);
                reason = Encoding.UTF8.GetString(payload, 2, payload.Length - 2);
                WriteServerFrame(session, 0x8, payload);
            });

            var socket = WebSocketClient.Connect($"ws://127.0.0.1:{server.Port}/", NoKeepAlive);
            socket.Close(WebSocketCloseStatus.Normal, "terminado");
            server.Wait();

            Assert.Equal(WebSocketState.Closed, socket.State);
            Assert.Equal(WebSocketCloseStatus.Normal, socket.CloseStatus);
            Assert.Equal("terminado", reason);
        }

        [Fact]
        public void NegotiatesSubProtocolsOverTls()
        {
            string? offered = null;
            using var server = new TestServer(session =>
            {
                session.StartTls(TestCertificate.Localhost);
                offered = Handshake(session, protocol: "json.v2")["sec-websocket-protocol"];
            });

            using var socket = WebSocketClient.Connect($"wss://localhost:{server.Port}/", new WebSocketClientOptions
            {
                SubProtocols = ["json.v1", "json.v2"],
                KeepAliveInterval = null,
                Connection = new ConnectionOptions { Tls = TestCertificate.TrustingOptions },
            });

            server.Wait();
            Assert.Equal("json.v1, json.v2", offered);
            Assert.Equal("json.v2", socket.SubProtocol);
        }

        [Fact]
        public void RejectsAWrongAcceptKey()
        {
            using var server = new TestServer(session =>
            {
                session.ReadUntilEmptyLine();
                session.Write("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: bm9wZQ==\r\n\r\n");
            });

            Assert.Throws<WebSocketException>(() => WebSocketClient.Connect($"ws://127.0.0.1:{server.Port}/", NoKeepAlive));
        }

        [Fact]
        public void ReportsRefusedUpgrades()
        {
            using var server = new TestServer(session =>
            {
                session.ReadUntilEmptyLine();
                session.Write("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\n\r\n");
            });

            var exception = Assert.Throws<WebSocketException>(() => WebSocketClient.Connect($"ws://127.0.0.1:{server.Port}/", NoKeepAlive));
            Assert.Contains("403", exception.Message);
        }

        [Fact]
        public void ClosesOnInvalidUtf8()
        {
            ushort status = 0;
            using var server = new TestServer(session =>
            {
                Handshake(session);
                WriteServerFrame(session, 0x1, [0xC3, 0x28]);
                var (opcode, payload, _) = ReadClientFrame(session);
                if (opcode == 0x8)
                    status = BinaryPrimitives.ReadUInt16BigEndian(payload);
            });

            using var socket = WebSocketClient.Connect($"ws://127.0.0.1:{server.Port}/", NoKeepAlive);
            Assert.Throws<WebSocketException>(() => socket.Receive());
            server.Wait();

            Assert.Equal((ushort)WebSocketCloseStatus.InvalidPayload, status);
        }

        [Fact]
        public void SendsKeepAlivePings()
        {
            int pings = 0;
            using var server = new TestServer(session =>
            {
                Handshake(session);
                for (int i = 0; i < 2; i++)
                {
                    if (ReadClientFrame(session).Opcode == 0x9)
                        pings++;
                }
            });

            using var socket = WebSocketClient.Connect($"ws://127.0.0.1:{server.Port}/", new WebSocketClientOptions { KeepAliveInterval = TimeSpan.FromMilliseconds(100) });
            server.Wait();
            Assert.Equal(2, pings);
        }

        // --- server side of the protocol ---------------------------------------------------------------------

        private static Dictionary<string, string> Handshake(ServerSession session, string? protocol = null)
        {
            session.ExpectStartsWith("GET ");
            var headers = session.ReadUntilEmptyLine()
                .Select(l => l.Split(':', 2))
                .ToDictionary(p => p[0].Trim().ToLowerInvariant(), p => p[1].Trim());

            string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(headers["sec-websocket-key"] + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            session.Write("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                          $"Sec-WebSocket-Accept: {accept}\r\n" + (protocol is null ? "" : $"Sec-WebSocket-Protocol: {protocol}\r\n") + "\r\n");
            return headers;
        }

        private static (byte Opcode, byte[] Payload, bool Fin) ReadClientFrame(ServerSession session)
        {
            byte[] header = session.ReadBytes(2);
            if ((header[1] & 0x80) == 0)
                throw new InvalidOperationException("The client sent an unmasked frame.");

            long length = header[1] & 0x7F;
            if (length == 126)
                length = BinaryPrimitives.ReadUInt16BigEndian(session.ReadBytes(2));
            else if (length == 127)
                length = (long)BinaryPrimitives.ReadUInt64BigEndian(session.ReadBytes(8));

            byte[] mask = session.ReadBytes(4);
            byte[] payload = session.ReadBytes((int)length);
            for (int i = 0; i < payload.Length; i++)
                payload[i] ^= mask[i & 3];

            return ((byte)(header[0] & 0x0F), payload, (header[0] & 0x80) != 0);
        }

        private static void WriteServerFrame(ServerSession session, byte opcode, byte[] payload, bool fin = true)
        {
            var frame = new List<byte> { (byte)((fin ? 0x80 : 0) | opcode) };
            if (payload.Length <= 125)
            {
                frame.Add((byte)payload.Length);
            }
            else if (payload.Length <= ushort.MaxValue)
            {
                frame.Add(126);
                frame.AddRange([(byte)(payload.Length >> 8), (byte)payload.Length]);
            }
            else
            {
                frame.Add(127);
                byte[] length = new byte[8];
                BinaryPrimitives.WriteUInt64BigEndian(length, (ulong)payload.Length);
                frame.AddRange(length);
            }

            frame.AddRange(payload);
            session.Write([.. frame]);
        }
    }
}

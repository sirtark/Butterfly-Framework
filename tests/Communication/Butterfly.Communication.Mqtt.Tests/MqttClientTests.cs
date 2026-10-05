using System.Collections.Concurrent;
using System.Text;

using Butterfly.Communication.Testing;

namespace Butterfly.Communication.Mqtt.Tests
{
    public class MqttClientTests
    {
        [Fact]
        public void PublishesSubscribesAndReceivesWithEveryQos()
        {
            byte[]? connect = null;
            var published = new List<(byte Header, byte[] Body)>();
            bool disconnected = false;
            using var incomingFlowDone = new ManualResetEventSlim();

            using var broker = new TestServer(session =>
            {
                connect = Read(session, out _);
                Send(session, 0x20, [0x01, 0x00]); // CONNACK, session present

                byte[] subscribe = Read(session, out byte subscribeHeader);
                Assert.Equal(0x82, subscribeHeader);
                Send(session, 0x90, [.. PacketId(subscribe), 0x01, 0x80]); // granted QoS 1, refused the second

                byte[] qos0 = Read(session, out byte h0); published.Add((h0, qos0));
                byte[] qos1 = Read(session, out byte h1); published.Add((h1, qos1));
                Send(session, 0x40, PublishId(qos1));                                                     // PUBACK
                byte[] qos2 = Read(session, out byte h2); published.Add((h2, qos2));
                Send(session, 0x50, PublishId(qos2));                                                     // PUBREC
                Assert.Equal(PublishId(qos2), Read(session, out byte rel));                               // PUBREL
                Assert.Equal(0x62, rel);
                Send(session, 0x70, PublishId(qos2));                                                     // PUBCOMP

                // Broker → client: QoS 1, then QoS 2 twice (a redelivery) before PUBREL.
                Send(session, 0x32, Publish("sensores/temp", 7, "21.5"));
                Assert.Equal([0, 7], Read(session, out byte pubAck)); Assert.Equal(0x40, pubAck);
                Send(session, 0x34, Publish("alertas/fuego", 9, "¡alarma!"));
                Read(session, out byte pubRec1); Assert.Equal(0x50, pubRec1);
                Send(session, 0x3C, Publish("alertas/fuego", 9, "¡alarma!"));                            // DUP
                Read(session, out byte pubRec2); Assert.Equal(0x50, pubRec2);
                Send(session, 0x62, [0, 9]);                                                              // PUBREL
                Assert.Equal([0, 9], Read(session, out byte pubComp)); Assert.Equal(0x70, pubComp);
                incomingFlowDone.Set();

                byte[] unsubscribe = Read(session, out byte unsubscribeHeader);
                Assert.Equal(0xA2, unsubscribeHeader);
                Send(session, 0xB0, PacketId(unsubscribe));

                Read(session, out byte last);
                disconnected = last == 0xE0;
            });

            var received = new BlockingCollection<MqttMessage>();
            try
            {
                using var client = new MqttClient();
                client.MessageReceived += received.Add;
                client.Connect(broker.Host, broker.Port, TlsMode.None, new MqttClientOptions
                {
                    ClientId = "sensor-1",
                    UserName = "ana",
                    Password = "clave",
                    CleanSession = false,
                    KeepAlive = TimeSpan.FromSeconds(30),
                    Will = new MqttMessage("estado/sensor-1", "offline", MqttQos.AtLeastOnce, retain: true),
                });
                Assert.True(client.SessionPresent);

                IReadOnlyList<MqttQos?> granted = client.Subscribe(("sensores/#", MqttQos.AtLeastOnce), ("prohibido/+", MqttQos.ExactlyOnce));
                Assert.Equal([MqttQos.AtLeastOnce, null], granted);

                client.Publish("luces/salon", "on");
                client.Publish("luces/salon", "off", MqttQos.AtLeastOnce);
                client.Publish("luces/salon", "dim", MqttQos.ExactlyOnce, retain: true);

                Assert.True(received.TryTake(out MqttMessage? first, 5000));
                Assert.True(received.TryTake(out MqttMessage? second, 5000));

                // Let the receiving thread finish the QoS 2 handshake (redelivery, PUBREL, PUBCOMP) first.
                Assert.True(incomingFlowDone.Wait(TimeSpan.FromSeconds(5)));
                Assert.False(received.TryTake(out _, 100)); // the QoS 2 redelivery was not delivered twice

                client.Unsubscribe("sensores/#");

                Assert.Equal(("sensores/temp", "21.5", MqttQos.AtLeastOnce), (first!.Topic, first.PayloadText, first.Qos));
                Assert.Equal(("alertas/fuego", "¡alarma!", MqttQos.ExactlyOnce), (second!.Topic, second.PayloadText, second.Qos));
            }
            catch (MqttException)
            {
                broker.Wait(); // surfaces the broker script's own failure, which is the real cause
                throw;
            }

            broker.Wait();

            // CONNECT: "MQTT", level 4, flags = user | password | will retain | will QoS 1 | will | (no clean session)
            Assert.Equal([0, 4, (byte)'M', (byte)'Q', (byte)'T', (byte)'T', 4, 0b1110_1100, 0, 30], connect![..10]);
            Assert.Equal(0x30, published[0].Header);
            Assert.Equal(0x32, published[1].Header);
            Assert.Equal(0x35, published[2].Header);
            Assert.True(disconnected);
        }

        [Fact]
        public void ReportsRefusedConnections()
        {
            using var broker = new TestServer(session =>
            {
                Read(session, out _);
                Send(session, 0x20, [0x00, 0x05]);
            });

            using var client = new MqttClient();
            var exception = Assert.Throws<MqttException>(() => client.Connect(broker.Host, broker.Port, TlsMode.None));
            Assert.Equal(MqttConnectReturnCode.NotAuthorized, exception.ReturnCode);
            Assert.False(client.IsConnected);
        }

        [Fact]
        public void SendsKeepAlivePings()
        {
            int pings = 0;
            using var broker = new TestServer(session =>
            {
                Read(session, out _);
                Send(session, 0x20, [0, 0]);
                for (int i = 0; i < 2; i++)
                {
                    Read(session, out byte header);
                    if (header == 0xC0)
                    {
                        pings++;
                        Send(session, 0xD0, []);
                    }
                }
            });

            using var client = new MqttClient();
            client.Connect(broker.Host, broker.Port, TlsMode.None, new MqttClientOptions { KeepAlive = TimeSpan.FromMilliseconds(400) });
            broker.Wait();
            Assert.Equal(2, pings);
        }

        [Fact]
        public void NotifiesWhenTheBrokerGoesAway()
        {
            using var broker = new TestServer(session =>
            {
                Read(session, out _);
                Send(session, 0x20, [0, 0]);
                Thread.Sleep(100);
            });

            using var lost = new ManualResetEventSlim();
            Exception? reason = null;
            using var client = new MqttClient();
            client.Disconnected += ex => { reason = ex; lost.Set(); };
            client.Connect(broker.Host, broker.Port, TlsMode.None, new MqttClientOptions { KeepAlive = TimeSpan.Zero });

            Assert.True(lost.Wait(TimeSpan.FromSeconds(5)));
            Assert.NotNull(reason);
            Assert.False(client.IsConnected);
            Assert.Throws<InvalidOperationException>(() => client.Publish("a/b", "x", MqttQos.AtLeastOnce));
        }

        [Theory]
        [InlineData("casa/+/temp", "casa/salon/temp", true)]
        [InlineData("casa/+/temp", "casa/salon/humedad", false)]
        [InlineData("casa/#", "casa", true)]
        [InlineData("casa/#", "casa/salon/temp", true)]
        [InlineData("#", "$SYS/broker/uptime", false)]
        [InlineData("$SYS/#", "$SYS/broker/uptime", true)]
        [InlineData("+/+", "casa", false)]
        public void MatchesTopicFilters(string filter, string topic, bool expected) => Assert.Equal(expected, MqttTopic.Matches(filter, topic));

        [Theory]
        [InlineData("casa/sal#on")]
        [InlineData("casa/#/temp")]
        [InlineData("")]
        public void RejectsInvalidFilters(string filter) => Assert.Throws<ArgumentException>(() => MqttTopic.ValidateFilter(filter));

        // --- broker side -------------------------------------------------------------------------------------

        private static byte[] Read(ServerSession session, out byte header)
        {
            header = session.ReadBytes(1)[0];
            int length = 0, multiplier = 1;
            byte digit;
            do
            {
                digit = session.ReadBytes(1)[0];
                length += (digit & 0x7F) * multiplier;
                multiplier *= 128;
            }
            while ((digit & 0x80) != 0);
            return session.ReadBytes(length);
        }

        private static void Send(ServerSession session, byte header, byte[] body)
            => session.Write([header, (byte)body.Length, .. body]);

        private static byte[] Publish(string topic, ushort id, string payload)
        {
            byte[] topicBytes = Encoding.UTF8.GetBytes(topic);
            return [(byte)(topicBytes.Length >> 8), (byte)topicBytes.Length, .. topicBytes, (byte)(id >> 8), (byte)id, .. Encoding.UTF8.GetBytes(payload)];
        }

        /// <summary>SUBSCRIBE and UNSUBSCRIBE start with the packet id.</summary>
        private static byte[] PacketId(byte[] body) => body[..2];

        /// <summary>A QoS 1/2 PUBLISH has the packet id right after the topic.</summary>
        private static byte[] PublishId(byte[] body)
        {
            int topicLength = body[0] << 8 | body[1];
            return body[(2 + topicLength)..(4 + topicLength)];
        }
    }
}

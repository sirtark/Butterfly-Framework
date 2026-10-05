using System.Text;

namespace Butterfly.Communication.Mqtt
{
    public enum MqttQos : byte
    {
        AtMostOnce = 0,
        AtLeastOnce = 1,
        ExactlyOnce = 2
    }

    public enum MqttConnectReturnCode : byte
    {
        Accepted = 0,
        UnacceptableProtocolVersion = 1,
        IdentifierRejected = 2,
        ServerUnavailable = 3,
        BadUserNameOrPassword = 4,
        NotAuthorized = 5
    }

    public sealed class MqttMessage(string topic, byte[] payload, MqttQos qos = MqttQos.AtMostOnce, bool retain = false)
    {
        public MqttMessage(string topic, string payload, MqttQos qos = MqttQos.AtMostOnce, bool retain = false)
            : this(topic, Encoding.UTF8.GetBytes(payload), qos, retain) { }

        public string Topic { get; } = topic;
        public byte[] Payload { get; } = payload;
        public MqttQos Qos { get; } = qos;

        /// <summary>Sent: the broker keeps it for future subscribers. Received: it is a retained message, not a live one.</summary>
        public bool Retain { get; } = retain;

        /// <summary>The broker may have delivered this message before (QoS 1 redelivery).</summary>
        public bool Duplicate { get; internal init; }

        public string PayloadText => Encoding.UTF8.GetString(Payload);

        public override string ToString() => $"{Topic} [{Qos}{(Retain ? ", retained" : "")}]: {Payload.Length} bytes";
    }

    public sealed class MqttClientOptions
    {
        /// <summary>Must be unique per broker. Empty generates a random one (only valid with <see cref="CleanSession"/>).</summary>
        public string ClientId { get; init; } = "";

        public string? UserName { get; init; }
        public string? Password { get; init; }

        /// <summary>A PINGREQ is sent when nothing else was sent for this long; <see cref="TimeSpan.Zero"/> disables keep-alive.</summary>
        public TimeSpan KeepAlive { get; init; } = TimeSpan.FromSeconds(60);

        /// <summary>Starts from scratch; false resumes the broker-side session (subscriptions and queued QoS 1/2 messages).</summary>
        public bool CleanSession { get; init; } = true;

        /// <summary>Published by the broker if this client disappears without disconnecting.</summary>
        public MqttMessage? Will { get; init; }

        /// <summary>How long Publish/Subscribe wait for the broker's acknowledgement.</summary>
        public TimeSpan AcknowledgementTimeout { get; init; } = TimeSpan.FromSeconds(30);
    }

    public class MqttException : CommunicationException
    {
        public MqttException(string message, Exception? innerException = null) : base(message, innerException) { }

        public MqttConnectReturnCode? ReturnCode { get; init; }
    }

    public static class MqttTopic
    {
        /// <summary>
        /// Whether <paramref name="topic"/> matches <paramref name="filter"/>: "+" matches one level, "#" the rest.
        /// Wildcards at the first level never match topics starting with "$" (such as $SYS).
        /// </summary>
        public static bool Matches(string filter, string topic)
        {
            string[] filterLevels = filter.Split('/');
            string[] topicLevels = topic.Split('/');

            if (topic.StartsWith('$') && filterLevels[0] is "+" or "#")
                return false;

            for (int i = 0; i < filterLevels.Length; i++)
            {
                if (filterLevels[i] == "#")
                    return true;
                if (i >= topicLevels.Length)
                    return false;
                if (filterLevels[i] != "+" && filterLevels[i] != topicLevels[i])
                    return false;
            }

            return filterLevels.Length == topicLevels.Length;
        }

        public static void ValidateTopic(string topic)
        {
            if (string.IsNullOrEmpty(topic) || topic.AsSpan().IndexOfAny('+', '#', '\0') >= 0)
                throw new ArgumentException($"'{topic}' is not a valid topic to publish to: it must not be empty or contain wildcards.", nameof(topic));
            if (Encoding.UTF8.GetByteCount(topic) > ushort.MaxValue)
                throw new ArgumentException("The topic is longer than 65535 bytes.", nameof(topic));
        }

        public static void ValidateFilter(string filter)
        {
            if (string.IsNullOrEmpty(filter) || filter.Contains('\0'))
                throw new ArgumentException("A topic filter must not be empty.", nameof(filter));

            string[] levels = filter.Split('/');
            for (int i = 0; i < levels.Length; i++)
            {
                bool hasWildcard = levels[i].Contains('+') || levels[i].Contains('#');
                if (hasWildcard && levels[i].Length != 1)
                    throw new ArgumentException($"'{filter}': wildcards must take a whole level.", nameof(filter));
                if (levels[i] == "#" && i != levels.Length - 1)
                    throw new ArgumentException($"'{filter}': '#' must be the last level.", nameof(filter));
            }
        }
    }
}

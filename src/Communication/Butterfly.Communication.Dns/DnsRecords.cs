using System.Text;

using Butterfly.Networking.Sockets;

namespace Butterfly.Communication.Dns
{
    /// <summary>A resource record. Types without a dedicated class keep their raw <see cref="Data"/>.</summary>
    public class DnsRecord(string name, DnsRecordType type, DnsClass @class, uint ttl, byte[] data)
    {
        public string Name { get; } = name;
        public DnsRecordType Type { get; } = type;
        public DnsClass Class { get; } = @class;
        public TimeSpan TimeToLive { get; } = TimeSpan.FromSeconds(ttl);

        /// <summary>RDATA exactly as received (compressed names inside it are not expanded).</summary>
        public byte[] Data { get; } = data;

        public override string ToString() => $"{Name} {TimeToLive.TotalSeconds} {Class} {Type} {DataToString()}";

        protected virtual string DataToString() => Convert.ToHexString(Data);
    }

    public sealed class AddressRecord(string name, DnsRecordType type, DnsClass @class, uint ttl, byte[] data, SocketAddress address)
        : DnsRecord(name, type, @class, ttl, data)
    {
        /// <summary>The address (port 0). A records are IPv4, AAAA records IPv6.</summary>
        public SocketAddress Address { get; } = address;

        protected override string DataToString() => Address.AddressToString();
    }

    /// <summary>CNAME, NS and PTR: a record whose data is a single domain name.</summary>
    public sealed class NameRecord(string name, DnsRecordType type, DnsClass @class, uint ttl, byte[] data, string target)
        : DnsRecord(name, type, @class, ttl, data)
    {
        public string Target { get; } = target;

        protected override string DataToString() => Target;
    }

    public sealed class MxRecord(string name, DnsClass @class, uint ttl, byte[] data, ushort preference, string exchange)
        : DnsRecord(name, DnsRecordType.MX, @class, ttl, data)
    {
        public ushort Preference { get; } = preference;
        public string Exchange { get; } = exchange;

        protected override string DataToString() => $"{Preference} {Exchange}";
    }

    public sealed class TxtRecord(string name, DnsClass @class, uint ttl, byte[] data, IReadOnlyList<string> strings)
        : DnsRecord(name, DnsRecordType.TXT, @class, ttl, data)
    {
        /// <summary>The character-strings of the record, in order.</summary>
        public IReadOnlyList<string> Strings { get; } = strings;

        /// <summary>The strings concatenated, which is how SPF, DKIM and similar records are meant to be read.</summary>
        public string Text => string.Concat(Strings);

        protected override string DataToString() => string.Join(' ', Strings.Select(s => $"\"{s}\""));
    }

    public sealed class SrvRecord(string name, DnsClass @class, uint ttl, byte[] data, ushort priority, ushort weight, ushort port, string target)
        : DnsRecord(name, DnsRecordType.SRV, @class, ttl, data)
    {
        public ushort Priority { get; } = priority;
        public ushort Weight { get; } = weight;
        public ushort Port { get; } = port;
        public string Target { get; } = target;

        protected override string DataToString() => $"{Priority} {Weight} {Port} {Target}";
    }

    public sealed class SoaRecord(string name, DnsClass @class, uint ttl, byte[] data,
        string primaryServer, string responsibleMailbox, uint serial, uint refresh, uint retry, uint expire, uint minimum)
        : DnsRecord(name, DnsRecordType.SOA, @class, ttl, data)
    {
        public string PrimaryServer { get; } = primaryServer;
        public string ResponsibleMailbox { get; } = responsibleMailbox;
        public uint Serial { get; } = serial;
        public TimeSpan Refresh { get; } = TimeSpan.FromSeconds(refresh);
        public TimeSpan Retry { get; } = TimeSpan.FromSeconds(retry);
        public TimeSpan Expire { get; } = TimeSpan.FromSeconds(expire);
        public TimeSpan MinimumTimeToLive { get; } = TimeSpan.FromSeconds(minimum);

        protected override string DataToString()
            => $"{PrimaryServer} {ResponsibleMailbox} {Serial} {Refresh.TotalSeconds} {Retry.TotalSeconds} {Expire.TotalSeconds} {MinimumTimeToLive.TotalSeconds}";
    }

    public sealed class CaaRecord(string name, DnsClass @class, uint ttl, byte[] data, byte flags, string tag, string value)
        : DnsRecord(name, DnsRecordType.CAA, @class, ttl, data)
    {
        public byte Flags { get; } = flags;
        public bool IsCritical => (Flags & 0x80) != 0;
        public string Tag { get; } = tag;
        public string Value { get; } = value;

        protected override string DataToString() => $"{Flags} {Tag} \"{Value}\"";
    }

    public sealed record DnsQuestion(string Name, DnsRecordType Type, DnsClass Class = DnsClass.IN);

    public sealed class DnsResponse
    {
        internal DnsResponse(ushort id, DnsResponseCode code, bool authoritative, bool truncated, bool recursionAvailable,
            IReadOnlyList<DnsQuestion> questions, IReadOnlyList<DnsRecord> answers, IReadOnlyList<DnsRecord> authorities, IReadOnlyList<DnsRecord> additionals)
        {
            Id = id;
            ResponseCode = code;
            IsAuthoritative = authoritative;
            IsTruncated = truncated;
            IsRecursionAvailable = recursionAvailable;
            Questions = questions;
            Answers = answers;
            Authorities = authorities;
            Additionals = additionals;
        }

        public ushort Id { get; }
        public DnsResponseCode ResponseCode { get; }
        public bool IsAuthoritative { get; }
        public bool IsTruncated { get; }
        public bool IsRecursionAvailable { get; }
        public IReadOnlyList<DnsQuestion> Questions { get; }
        public IReadOnlyList<DnsRecord> Answers { get; }
        public IReadOnlyList<DnsRecord> Authorities { get; }
        public IReadOnlyList<DnsRecord> Additionals { get; }

        /// <summary>The answers of type <typeparamref name="T"/>, e.g. <c>response.AnswersOf&lt;MxRecord&gt;()</c>.</summary>
        public IEnumerable<T> AnswersOf<T>() where T : DnsRecord => Answers.OfType<T>();

        public override string ToString()
        {
            var builder = new StringBuilder($";; {ResponseCode}, id {Id}{(IsAuthoritative ? ", authoritative" : "")}{(IsTruncated ? ", truncated" : "")}");
            foreach (DnsRecord record in Answers)
                builder.AppendLine().Append(record);
            return builder.ToString();
        }
    }

    public class DnsException : Exception
    {
        public DnsException(string message, Exception? innerException = null) : base(message, innerException) { }

        public DnsException(string message, DnsResponseCode responseCode) : base(message) => ResponseCode = responseCode;

        /// <summary>The server's response code, when the failure came from the server.</summary>
        public DnsResponseCode? ResponseCode { get; }
    }
}

using System.Buffers.Binary;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;

using Butterfly.Networking.Sockets;

using NetAddressFamily = System.Net.Sockets.AddressFamily;

namespace Butterfly.Communication.Dns
{
    public sealed class DnsClientOptions
    {
        public const ushort DefaultPort = 53;

        /// <summary>Servers to ask, in order. Empty (the default) uses the servers configured in the operating system.</summary>
        public IReadOnlyList<SocketAddress> Servers { get; init; } = [];

        /// <summary>How long to wait for each server on each attempt.</summary>
        public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(2);

        /// <summary>How many rounds over the server list are made before giving up.</summary>
        public int Attempts { get; init; } = 2;

        public bool RecursionDesired { get; init; } = true;

        /// <summary>Repeats the query over TCP when the UDP answer is truncated.</summary>
        public bool UseTcpOnTruncation { get; init; } = true;

        /// <summary>Largest UDP answer announced through EDNS(0); 0 disables EDNS. 1232 avoids IP fragmentation (DNS Flag Day 2020).</summary>
        public ushort UdpPayloadSize { get; init; } = 1232;
    }

    public sealed class DnsClient
    {
        private static readonly Lazy<DnsClient> s_default = new(() => new DnsClient());

        private readonly DnsClientOptions _options;

        public DnsClient(DnsClientOptions? options = null)
        {
            _options = options ?? new DnsClientOptions();
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.Attempts, nameof(DnsClientOptions.Attempts));
            Servers = _options.Servers.Count > 0 ? _options.Servers : GetSystemServers();
        }

        /// <summary>A client using the operating system's DNS servers.</summary>
        public static DnsClient Default => s_default.Value;

        public IReadOnlyList<SocketAddress> Servers { get; }

        public DnsResponse Query(string name, DnsRecordType type, DnsClass @class = DnsClass.IN, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            if (Servers.Count == 0)
                throw new DnsException("No DNS servers are configured.");

            var question = new DnsQuestion(DnsMessage.ToAscii(name.TrimEnd('.')), type, @class);
            DnsResponse? lastResponse = null;
            Exception? lastError = null;

            for (int attempt = 0; attempt < _options.Attempts; attempt++)
            {
                foreach (SocketAddress server in Servers)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        DnsResponse response = QueryServer(server, question);

                        // NXDOMAIN is a definitive answer; SERVFAIL and REFUSED are worth asking someone else.
                        if (response.ResponseCode is DnsResponseCode.NoError or DnsResponseCode.NameError)
                            return response;

                        lastResponse = response;
                    }
                    catch (Exception ex) when (ex is SocketException or DnsException)
                    {
                        lastError = ex;
                    }
                }
            }

            if (lastResponse is not null)
                throw new DnsException($"DNS query for {name} {type} failed: {lastResponse.ResponseCode}.", lastResponse.ResponseCode);

            throw new DnsException($"No DNS server answered the query for {name} {type}.", lastError);
        }

        public Task<DnsResponse> QueryAsync(string name, DnsRecordType type, DnsClass @class = DnsClass.IN, CancellationToken cancellationToken = default)
            => Task.Run(() => Query(name, type, @class, cancellationToken), cancellationToken);

        /// <summary>The IPv4 and IPv6 addresses of <paramref name="host"/> (CNAME chains are followed by the server).</summary>
        /// <exception cref="DnsException">The name does not exist or no server answered.</exception>
        public IReadOnlyList<SocketAddress> ResolveAddresses(string host, CancellationToken cancellationToken = default)
        {
            var addresses = new List<SocketAddress>();
            DnsResponse ipv4 = Query(host, DnsRecordType.A, DnsClass.IN, cancellationToken);
            if (ipv4.ResponseCode == DnsResponseCode.NameError)
                throw new DnsException($"The name '{host}' does not exist.", DnsResponseCode.NameError);

            addresses.AddRange(ipv4.AnswersOf<AddressRecord>().Where(r => r.Type == DnsRecordType.A).Select(r => r.Address));

            DnsResponse ipv6 = Query(host, DnsRecordType.AAAA, DnsClass.IN, cancellationToken);
            addresses.AddRange(ipv6.AnswersOf<AddressRecord>().Where(r => r.Type == DnsRecordType.AAAA).Select(r => r.Address));

            return addresses;
        }

        public Task<IReadOnlyList<SocketAddress>> ResolveAddressesAsync(string host, CancellationToken cancellationToken = default)
            => Task.Run(() => ResolveAddresses(host, cancellationToken), cancellationToken);

        /// <summary>The names registered for an address (PTR records under in-addr.arpa or ip6.arpa).</summary>
        public IReadOnlyList<string> ReverseLookup(SocketAddress address, CancellationToken cancellationToken = default)
        {
            DnsResponse response = Query(ReverseName(address), DnsRecordType.PTR, DnsClass.IN, cancellationToken);
            return [.. response.AnswersOf<NameRecord>().Select(r => r.Target)];
        }

        public static string ReverseName(SocketAddress address)
        {
            byte[] bytes = address.GetAddressBytes();
            var builder = new StringBuilder();

            if (address.Family == AddressFamily.IPv4)
            {
                for (int i = bytes.Length - 1; i >= 0; i--)
                    builder.Append(bytes[i]).Append('.');
                return builder.Append("in-addr.arpa").ToString();
            }

            for (int i = bytes.Length - 1; i >= 0; i--)
                builder.Append("0123456789abcdef"[bytes[i] & 0xF]).Append('.').Append("0123456789abcdef"[bytes[i] >> 4]).Append('.');
            return builder.Append("ip6.arpa").ToString();
        }

        /// <summary>The DNS servers of the network interfaces that are up (on Unix, the ones in /etc/resolv.conf).</summary>
        public static IReadOnlyList<SocketAddress> GetSystemServers()
        {
            var servers = new List<SocketAddress>();
            try
            {
                foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                        continue;

                    foreach (System.Net.IPAddress address in adapter.GetIPProperties().DnsAddresses)
                    {
                        // fec0:0:0:ffff::1-3 are deprecated site-local placeholders Windows lists when nothing is configured.
                        if (address.AddressFamily == NetAddressFamily.InterNetworkV6 && address.IsIPv6SiteLocal)
                            continue;

                        var server = new SocketAddress(address.GetAddressBytes(), DnsClientOptions.DefaultPort,
                            address.AddressFamily == NetAddressFamily.InterNetworkV6 ? (uint)address.ScopeId : 0);

                        if (!servers.Contains(server))
                            servers.Add(server);
                    }
                }
            }
            catch (NetworkInformationException)
            {
                // No interface information available: the caller reports that no servers are configured.
            }

            // Prefer IPv4 servers: they work on every network where IPv6 might be half-configured.
            return [.. servers.OrderBy(s => s.Family == AddressFamily.IPv4 ? 0 : 1)];
        }

        private DnsResponse QueryServer(SocketAddress server, DnsQuestion question)
        {
            ushort id = (ushort)RandomNumberGenerator.GetInt32(ushort.MaxValue + 1);
            byte[] query = DnsMessage.EncodeQuery(id, question, _options.RecursionDesired, _options.UdpPayloadSize);

            DnsResponse response = QueryUdp(server, query, id, question);
            if (response.IsTruncated && _options.UseTcpOnTruncation)
                response = QueryTcp(server, query, id, question);

            return response;
        }

        private DnsResponse QueryUdp(SocketAddress server, byte[] query, ushort id, DnsQuestion question)
        {
            using var socket = new UdpSocket(server.Family) { ReceiveTimeout = _options.Timeout };
            socket.SendTo(query, server);

            byte[] buffer = new byte[Math.Max((int)_options.UdpPayloadSize, 512)];
            long deadline = Environment.TickCount64 + (long)_options.Timeout.TotalMilliseconds;

            while (true)
            {
                int received = socket.ReceiveFrom(buffer, out SocketAddress from);

                // Ignore stray or spoofed datagrams: they must come from the server and echo the id and question.
                if (from.Port == server.Port && from.AddressToString() == server.AddressToString())
                {
                    DnsResponse? response = TryMatch(buffer.AsSpan(0, received), id, question);
                    if (response is not null)
                        return response;
                }

                if (Environment.TickCount64 >= deadline)
                    throw new SocketException(SocketError.TimedOut, 0, "recvfrom");
            }
        }

        private DnsResponse QueryTcp(SocketAddress server, byte[] query, ushort id, DnsQuestion question)
        {
            using var socket = new TcpSocket(server.Family) { ReceiveTimeout = _options.Timeout, SendTimeout = _options.Timeout };
            socket.Connect(server, _options.Timeout);

            // Over TCP every message is preceded by its length (RFC 1035 4.2.2).
            byte[] framed = new byte[query.Length + 2];
            BinaryPrimitives.WriteUInt16BigEndian(framed, (ushort)query.Length);
            query.CopyTo(framed, 2);
            for (int sent = 0; sent < framed.Length;)
                sent += socket.Send(framed.AsSpan(sent));

            byte[] lengthBytes = ReceiveExactly(socket, 2);
            byte[] message = ReceiveExactly(socket, BinaryPrimitives.ReadUInt16BigEndian(lengthBytes));

            return TryMatch(message, id, question)
                ?? throw new DnsException("The DNS server answered a different query over TCP.");
        }

        private static DnsResponse? TryMatch(ReadOnlySpan<byte> message, ushort id, DnsQuestion question)
        {
            if (message.Length < DnsMessage.HeaderLength || BinaryPrimitives.ReadUInt16BigEndian(message) != id)
                return null;

            DnsResponse response = DnsMessage.DecodeResponse(message);
            bool sameQuestion = response.Questions.Count == 1
                && string.Equals(response.Questions[0].Name, question.Name, StringComparison.OrdinalIgnoreCase)
                && response.Questions[0].Type == question.Type;

            // Some servers omit the question in error responses; accept those only when they carry an error.
            return sameQuestion || (response.Questions.Count == 0 && response.ResponseCode != DnsResponseCode.NoError) ? response : null;
        }

        private static byte[] ReceiveExactly(TcpSocket socket, int count)
        {
            byte[] buffer = new byte[count];
            for (int read = 0; read < count;)
            {
                int received = socket.Receive(buffer.AsSpan(read));
                if (received == 0)
                    throw new DnsException("The DNS server closed the TCP connection before answering.");
                read += received;
            }
            return buffer;
        }
    }
}

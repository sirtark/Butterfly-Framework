using Butterfly.Communication.Dns;
using Butterfly.Networking.Sockets;

using NetAddressFamily = System.Net.Sockets.AddressFamily;

namespace Butterfly.Communication
{
    /// <summary>Turns a host name into addresses. The returned addresses carry port 0.</summary>
    public interface IHostResolver
    {
        IReadOnlyList<SocketAddress> Resolve(string host, CancellationToken cancellationToken = default);
    }

    public static class HostResolver
    {
        /// <summary>
        /// Used by every client unless <see cref="ConnectionOptions.Resolver"/> says otherwise: IP literals,
        /// "localhost", the hosts file, then Butterfly's own DNS client, and finally the operating system resolver.
        /// </summary>
        public static IHostResolver Default { get; set; } = new DefaultHostResolver(DnsClient.Default);

        /// <summary>Only the operating system resolver (getaddrinfo / GetAddrInfoExW).</summary>
        public static IHostResolver System { get; } = new SystemHostResolver();

        /// <summary>Only Butterfly's DNS client, with the given servers.</summary>
        public static IHostResolver FromDns(DnsClient client) => new DnsHostResolver(client);

        internal static bool TryResolveWithoutNetwork(string host, out IReadOnlyList<SocketAddress> addresses)
        {
            if (SocketAddress.TryParse(host, 0, out SocketAddress literal))
            {
                addresses = [literal];
                return true;
            }

            // RFC 6761: "localhost" and its subdomains are always the loopback addresses.
            if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            {
                addresses = [SocketAddress.Loopback(AddressFamily.IPv4, 0), SocketAddress.Loopback(AddressFamily.IPv6, 0)];
                return true;
            }

            addresses = [];
            return false;
        }
    }

    internal sealed class DefaultHostResolver(DnsClient dns) : IHostResolver
    {
        public IReadOnlyList<SocketAddress> Resolve(string host, CancellationToken cancellationToken = default)
        {
            host = host.TrimEnd('.');
            if (HostResolver.TryResolveWithoutNetwork(host, out var addresses))
                return addresses;

            IReadOnlyList<SocketAddress> fromHostsFile = HostsFile.Lookup(host);
            if (fromHostsFile.Count > 0)
                return fromHostsFile;

            try
            {
                if (dns.Servers.Count > 0)
                {
                    addresses = dns.ResolveAddresses(host, cancellationToken);
                    if (addresses.Count > 0)
                        return addresses;
                }
            }
            catch (DnsException)
            {
                // Search domains, mDNS (.local), NetBIOS or VPN split DNS are only known to the OS resolver.
            }

            return HostResolver.System.Resolve(host, cancellationToken);
        }
    }

    internal sealed class DnsHostResolver(DnsClient dns) : IHostResolver
    {
        public IReadOnlyList<SocketAddress> Resolve(string host, CancellationToken cancellationToken = default)
            => HostResolver.TryResolveWithoutNetwork(host, out var addresses) ? addresses : dns.ResolveAddresses(host, cancellationToken);
    }

    internal sealed class SystemHostResolver : IHostResolver
    {
        public IReadOnlyList<SocketAddress> Resolve(string host, CancellationToken cancellationToken = default)
        {
            if (HostResolver.TryResolveWithoutNetwork(host, out var addresses))
                return addresses;

            try
            {
                return [.. System.Net.Dns.GetHostAddresses(host)
                    .Where(a => a.AddressFamily is NetAddressFamily.InterNetwork or NetAddressFamily.InterNetworkV6)
                    .Select(a => new SocketAddress(a.GetAddressBytes(), 0, a.AddressFamily == NetAddressFamily.InterNetworkV6 ? (uint)a.ScopeId : 0))];
            }
            catch (System.Net.Sockets.SocketException ex)
            {
                throw new DnsException($"The host '{host}' could not be resolved.", ex);
            }
        }
    }

    /// <summary>The hosts file (/etc/hosts or %SystemRoot%\System32\drivers\etc\hosts), re-read when it changes.</summary>
    internal static class HostsFile
    {
        private static readonly Lock s_lock = new();
        private static DateTime s_lastWrite;
        private static Dictionary<string, List<SocketAddress>> s_entries = new(StringComparer.OrdinalIgnoreCase);

        public static IReadOnlyList<SocketAddress> Lookup(string host)
        {
            string path = OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts")
                : "/etc/hosts";

            try
            {
                DateTime lastWrite = File.GetLastWriteTimeUtc(path);
                lock (s_lock)
                {
                    if (lastWrite != s_lastWrite)
                    {
                        s_entries = File.Exists(path) ? Parse(File.ReadAllLines(path)) : new(StringComparer.OrdinalIgnoreCase);
                        s_lastWrite = lastWrite;
                    }

                    return s_entries.TryGetValue(host, out var addresses) ? addresses : [];
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return [];
            }
        }

        internal static Dictionary<string, List<SocketAddress>> Parse(IEnumerable<string> lines)
        {
            var entries = new Dictionary<string, List<SocketAddress>>(StringComparer.OrdinalIgnoreCase);
            foreach (string rawLine in lines)
            {
                int comment = rawLine.IndexOf('#');
                string[] fields = (comment >= 0 ? rawLine[..comment] : rawLine)
                    .Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);

                if (fields.Length < 2 || !SocketAddress.TryParse(fields[0], 0, out SocketAddress address))
                    continue;

                foreach (string name in fields.Skip(1))
                {
                    if (!entries.TryGetValue(name, out var list))
                        entries[name] = list = [];
                    if (!list.Contains(address))
                        list.Add(address);
                }
            }

            return entries;
        }
    }
}

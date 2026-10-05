using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace Butterfly.Communication
{
    /// <summary>How a connection is made. Shared by every protocol client.</summary>
    public sealed class ConnectionOptions
    {
        public static ConnectionOptions Default { get; } = new();

        /// <summary>Total time allowed to resolve the host and establish the connection (all addresses included).</summary>
        public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(30);

        /// <summary>How long a single read may wait for data. <see cref="Timeout.InfiniteTimeSpan"/> waits forever.</summary>
        public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(60);

        /// <summary>How long a single write may wait for the network.</summary>
        public TimeSpan WriteTimeout { get; init; } = TimeSpan.FromSeconds(60);

        /// <summary>Resolves host names; <see cref="HostResolver.Default"/> unless replaced.</summary>
        public IHostResolver? Resolver { get; init; }

        public TlsOptions Tls { get; init; } = new();

        public bool NoDelay { get; init; } = true;

        internal IHostResolver EffectiveResolver => Resolver ?? HostResolver.Default;
    }

    public sealed class TlsOptions
    {
        /// <summary>Protocols allowed; <see cref="SslProtocols.None"/> (the default) lets the operating system choose.</summary>
        public SslProtocols EnabledProtocols { get; init; } = SslProtocols.None;

        public X509CertificateCollection? ClientCertificates { get; init; }

        /// <summary>
        /// Replaces the default certificate validation. Leave it null in production: the default rejects
        /// untrusted, expired and mismatched certificates.
        /// </summary>
        public RemoteCertificateValidationCallback? RemoteCertificateValidation { get; init; }

        public X509RevocationMode RevocationMode { get; init; } = X509RevocationMode.NoCheck;

        /// <summary>
        /// Accepts any server certificate. Only for tests or servers you control: it removes the protection
        /// TLS gives against impersonation.
        /// </summary>
        public static TlsOptions InsecureAcceptAnyCertificate { get; } = new() { RemoteCertificateValidation = (_, _, _, _) => true };
    }

    /// <summary>How a protocol client secures its connection.</summary>
    public enum TlsMode
    {
        /// <summary>TLS right after connecting on the protocol's implicit TLS port (465, 993, 995, 990, 8883), otherwise <see cref="StartTls"/>.</summary>
        Auto,

        /// <summary>Plain text. Credentials and data travel unencrypted.</summary>
        None,

        /// <summary>TLS from the first byte (SMTPS, IMAPS, POP3S, FTPS implicit, MQTTS, HTTPS).</summary>
        Implicit,

        /// <summary>Connect in plain text and upgrade with STARTTLS/STLS/AUTH TLS; fails if the server does not offer it.</summary>
        StartTls,

        /// <summary>Upgrade when the server offers it, otherwise continue in plain text (vulnerable to downgrade attacks).</summary>
        StartTlsWhenAvailable
    }
}

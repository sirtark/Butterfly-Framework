using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Butterfly.Communication.Testing
{
    /// <summary>A self-signed certificate for "localhost" / 127.0.0.1, created once per test run.</summary>
    public static class TestCertificate
    {
        private static readonly Lazy<X509Certificate2> s_certificate = new(Create);

        public static X509Certificate2 Localhost => s_certificate.Value;

        /// <summary>Client options that trust exactly this certificate.</summary>
        public static TlsOptions TrustingOptions => new()
        {
            RemoteCertificateValidation = (_, certificate, _, _) => certificate is not null && certificate.GetCertHashString() == Localhost.GetCertHashString()
        };

        private static X509Certificate2 Create()
        {
            using RSA rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName("localhost");
            names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));

            using X509Certificate2 created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

            // Windows' TLS stack needs a key that is not ephemeral: round-trip through PKCS#12.
            return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), password: null);
        }
    }
}

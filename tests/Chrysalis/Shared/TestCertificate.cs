using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Butterfly.Chrysalis.Tests
{
    // A self-signed certificate for localhost/127.0.0.1, created once per test run.
    internal static class TestCertificate
    {
        private static readonly Lazy<X509Certificate2> Certificate = new(Create);

        public static X509Certificate2 Value => Certificate.Value;

        private static X509Certificate2 Create()
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName("localhost");
            names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            // A PFX round trip gives SChannel a key it can use for TLS server authentication.
            return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null);
        }
    }
}
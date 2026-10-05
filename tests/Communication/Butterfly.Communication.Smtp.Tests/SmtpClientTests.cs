using Butterfly.Communication.Mail;
using Butterfly.Communication.Testing;

namespace Butterfly.Communication.Smtp.Tests
{
    public class SmtpClientTests
    {
        private static SmtpClient NewClient(TlsOptions? tls = null)
            => new(new ConnectionOptions { Tls = tls ?? new TlsOptions() }) { ClientName = "cliente.test" };

        [Fact]
        public void SendsAMessageEndToEnd()
        {
            var data = new List<string>();
            string? mailFrom = null;
            bool quit = false;

            using var server = new TestServer(session =>
            {
                session.WriteLine("220 smtp.test ESMTP listo");
                session.Expect("EHLO cliente.test");
                session.Write("250-smtp.test hola\r\n250-SIZE 1000000\r\n250-AUTH PLAIN LOGIN\r\n250 8BITMIME\r\n");
                session.Expect("AUTH PLAIN " + Sasl.Plain("ana", "clave"));
                session.WriteLine("235 2.7.0 Autenticado");
                mailFrom = session.ReadLine();
                session.WriteLine("250 2.1.0 Ok");
                session.Expect("RCPT TO:<bueno@example.org>");
                session.WriteLine("250 2.1.5 Ok");
                session.Expect("RCPT TO:<malo@example.org>");
                session.WriteLine("550 5.1.1 No existe");
                session.Expect("DATA");
                session.WriteLine("354 Adelante");
                for (string line = session.ReadLine(); line != "."; line = session.ReadLine())
                    data.Add(line);
                session.WriteLine("250 2.0.0 Encolado como ABC123");
                quit = session.ReadLine() == "QUIT";
                session.WriteLine("221 Adiós");
            });

            var message = new MailMessage { From = "ana@example.com", Subject = "Prueba", TextBody = "Hola\n.linea con punto\nfin" };
            message.To.Add("bueno@example.org");
            message.Bcc.Add("malo@example.org");

            SmtpSendResult result;
            using (var client = NewClient())
            {
                client.Connect(server.Host, server.Port, TlsMode.None);
                Assert.Equal(1_000_000, client.MaxMessageSize);
                Assert.Equal(["PLAIN", "LOGIN"], client.AuthenticationMechanisms);

                client.Authenticate("ana", "clave");
                result = client.Send(message);
            }
            server.Wait();

            Assert.StartsWith("MAIL FROM:<ana@example.com> SIZE=", mailFrom);
            Assert.Equal("bueno@example.org", Assert.Single(result.Accepted).Address);
            Assert.Equal(550, Assert.Single(result.Rejected).Response.Code);
            Assert.Equal("5.1.1", result.Rejected[0].Response.EnhancedCode);
            Assert.Contains("Encolado", result.Response.Message);
            Assert.Contains("..linea con punto", data);
            Assert.DoesNotContain(data, l => l.Contains("malo@example.org"));
            Assert.True(quit);
        }

        [Fact]
        public void UpgradesWithStartTlsAndGreetsAgain()
        {
            using var server = new TestServer(session =>
            {
                session.WriteLine("220 listo");
                session.Expect("EHLO cliente.test");
                session.Write("250-hola\r\n250 STARTTLS\r\n");
                session.Expect("STARTTLS");
                session.WriteLine("220 Empieza TLS");
                session.StartTls(TestCertificate.Localhost);
                session.Expect("EHLO cliente.test");
                session.Write("250-hola de nuevo\r\n250 AUTH LOGIN\r\n");
                session.Expect("AUTH LOGIN");
                session.WriteLine("334 VXNlcm5hbWU6");
                session.Expect(Sasl.LoginStep("ana"));
                session.WriteLine("334 UGFzc3dvcmQ6");
                session.Expect(Sasl.LoginStep("clave"));
                session.WriteLine("235 Ok");
            });

            using var client = NewClient(TestCertificate.TrustingOptions);
            client.Connect("localhost", server.Port, TlsMode.StartTls);
            Assert.True(client.IsSecure);
            Assert.Equal(["LOGIN"], client.AuthenticationMechanisms);

            client.Authenticate("ana", "clave");
            Assert.True(client.IsAuthenticated);
            server.Wait();
        }

        [Fact]
        public void RequiredStartTlsFailsWhenNotOffered()
        {
            using var server = new TestServer(session =>
            {
                session.WriteLine("220 listo");
                session.ReadLine();
                session.WriteLine("250 hola");
            });

            using var client = NewClient();
            Assert.Throws<CommunicationException>(() => client.Connect(server.Host, server.Port, TlsMode.StartTls));
            Assert.False(client.IsConnected);
        }

        [Fact]
        public void SupportsImplicitTls()
        {
            using var server = new TestServer(session =>
            {
                session.StartTls(TestCertificate.Localhost);
                session.WriteLine("220 smtps");
                session.ReadLine();
                session.WriteLine("250 hola");
            });

            using var client = NewClient(TestCertificate.TrustingOptions);
            client.Connect("localhost", server.Port, TlsMode.Implicit);
            Assert.True(client.IsSecure);
            server.Wait();
        }

        [Fact]
        public void ThrowsWhenEveryRecipientIsRejected()
        {
            bool reset = false;
            using var server = new TestServer(session =>
            {
                session.WriteLine("220 listo");
                session.ReadLine();
                session.WriteLine("250 hola");
                session.ReadLine();
                session.WriteLine("250 Ok");
                session.ReadLine();
                session.WriteLine("550 5.7.1 Relay denegado");
                reset = session.ReadLine() == "RSET";
                session.WriteLine("250 Ok");
            });

            using var client = NewClient();
            client.Connect(server.Host, server.Port, TlsMode.None);
            var exception = Assert.Throws<SmtpRecipientsRejectedException>(
                () => client.Send(new MailAddress("a@x.com"), [new MailAddress("b@y.com")], "Subject: x\r\n\r\nhola"u8));

            Assert.Equal(550, exception.StatusCode);
            Assert.True(reset);
        }

        [Fact]
        public void ReportsAuthenticationFailures()
        {
            using var server = new TestServer(session =>
            {
                session.WriteLine("220 listo");
                session.ReadLine();
                session.Write("250-hola\r\n250 AUTH CRAM-MD5 PLAIN\r\n");
                session.Expect("AUTH CRAM-MD5");
                session.WriteLine("334 " + Sasl.ToBase64("<123@smtp.test>"));
                session.ReadLine();
                session.WriteLine("535 5.7.8 Credenciales incorrectas");
            });

            using var client = NewClient();
            client.Connect(server.Host, server.Port, TlsMode.None);
            var exception = Assert.Throws<SmtpException>(() => client.Authenticate("ana", "mala"));

            Assert.Equal(535, exception.StatusCode);
            Assert.False(client.IsAuthenticated);
        }

        [Fact]
        public void FallsBackToHeloOnOldServers()
        {
            using var server = new TestServer(session =>
            {
                session.WriteLine("220 servidor antiguo");
                session.Expect("EHLO cliente.test");
                session.WriteLine("502 No entiendo");
                session.Expect("HELO cliente.test");
                session.WriteLine("250 hola");
            });

            using var client = NewClient();
            client.Connect(server.Host, server.Port, TlsMode.None);
            Assert.Empty(client.Capabilities);
            server.Wait();
        }
    }
}

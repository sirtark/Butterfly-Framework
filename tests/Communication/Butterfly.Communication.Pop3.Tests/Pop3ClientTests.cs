using Butterfly.Communication.Testing;

namespace Butterfly.Communication.Pop3.Tests
{
    public class Pop3ClientTests
    {
        [Fact]
        public void ListsRetrievesAndDeletes()
        {
            bool quit = false;
            using var server = new TestServer(session =>
            {
                session.WriteLine("+OK POP3 listo");
                session.Expect("CAPA");
                session.Write("+OK\r\nUSER\r\nUIDL\r\nTOP\r\n.\r\n");
                session.Expect("USER ana");
                session.WriteLine("+OK");
                session.Expect("PASS clave");
                session.WriteLine("+OK 2 mensajes");
                session.Expect("STAT");
                session.WriteLine("+OK 2 350");
                session.Expect("LIST");
                session.Write("+OK\r\n1 120\r\n2 230\r\n.\r\n");
                session.Expect("UIDL");
                session.Write("+OK\r\n1 uid-a\r\n2 uid-b\r\n.\r\n");
                session.Expect("RETR 2");
                session.Write("+OK\r\nFrom: b@x.com\r\nSubject: Segundo\r\n\r\nHola\r\n..punto inicial\r\n.\r\n");
                session.Expect("TOP 1 0");
                session.Write("+OK\r\nSubject: Primero\r\n\r\n.\r\n");
                session.Expect("DELE 1");
                session.WriteLine("+OK borrado");
                quit = session.ReadLine() == "QUIT";
                session.WriteLine("+OK adiós");
            });

            using (var client = new Pop3Client())
            {
                client.Connect(server.Host, server.Port, TlsMode.None);
                client.Authenticate("ana", "clave");

                Assert.Equal((2, 350L), client.GetStatus());
                Assert.Equal([new Pop3MessageInfo(1, 120, "uid-a"), new Pop3MessageInfo(2, 230, "uid-b")], client.ListMessages());

                var message = client.Retrieve(2);
                Assert.Equal("Segundo", message.Subject);
                Assert.Equal("Hola\r\n.punto inicial\r\n", message.TextBody);

                Assert.Equal("Primero", client.RetrieveHeaders(1).Subject);
                client.Delete(1);
            }

            server.Wait();
            Assert.True(quit);
        }

        [Fact]
        public void UsesStlsAndSaslPlain()
        {
            using var server = new TestServer(session =>
            {
                session.WriteLine("+OK listo");
                session.Expect("CAPA");
                session.Write("+OK\r\nSTLS\r\n.\r\n");
                session.Expect("STLS");
                session.WriteLine("+OK comienza TLS");
                session.StartTls(TestCertificate.Localhost);
                session.Expect("CAPA");
                session.Write("+OK\r\nSASL PLAIN XOAUTH2\r\n.\r\n");
                session.Expect("AUTH PLAIN " + Sasl.Plain("ana", "clave"));
                session.WriteLine("+OK dentro");
            });

            using var client = new Pop3Client(new ConnectionOptions { Tls = TestCertificate.TrustingOptions });
            client.Connect("localhost", server.Port, TlsMode.StartTls);
            Assert.True(client.IsSecure);
            Assert.Equal(["PLAIN", "XOAUTH2"], client.SaslMechanisms);

            client.Authenticate("ana", "clave");
            Assert.True(client.IsAuthenticated);
            server.Wait();
        }

        [Fact]
        public void ReportsServerErrors()
        {
            using var server = new TestServer(session =>
            {
                session.WriteLine("+OK listo");
                session.Expect("CAPA");
                session.WriteLine("-ERR no soportado");
                session.Expect("USER ana");
                session.WriteLine("+OK");
                session.Expect("PASS mala");
                session.WriteLine("-ERR [AUTH] credenciales inválidas");
            });

            using var client = new Pop3Client();
            client.Connect(server.Host, server.Port, TlsMode.None);
            Assert.Empty(client.Capabilities);

            var exception = Assert.Throws<Pop3Exception>(() => client.Authenticate("ana", "mala"));
            Assert.Equal("[AUTH] credenciales inválidas", exception.ServerMessage);
        }
    }
}

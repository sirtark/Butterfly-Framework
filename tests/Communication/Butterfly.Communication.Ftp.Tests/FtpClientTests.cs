using System.Text;

using Butterfly.Communication.Testing;
using Butterfly.Networking.Sockets;

namespace Butterfly.Communication.Ftp.Tests
{
    public class FtpClientTests
    {
        [Fact]
        public void BrowsesDownloadsAndUploads()
        {
            string? uploaded = null;
            using var server = new TestServer(session =>
            {
                session.WriteLine("220 FTP listo");
                session.Expect("FEAT");
                session.Write("211-Features:\r\n MLST type*;size*;modify*;\r\n EPSV\r\n SIZE\r\n UTF8\r\n211 End\r\n");
                session.Expect("USER ana");
                session.WriteLine("331 Contraseña requerida");
                session.Expect("PASS clave");
                session.WriteLine("230 Dentro");
                session.Expect("OPTS UTF8 ON");
                session.WriteLine("200 Ok");
                session.Expect("TYPE I");
                session.WriteLine("200 Binario");
                session.Expect("PWD");
                session.WriteLine("257 \"/home/ana \"\"docs\"\"\" es el actual");

                Passive(session, "MLSD", data => data.Write(
                    "type=cdir;modify=20260101000000; .\r\n" +
                    "type=dir;modify=20260102030405; informes\r\n" +
                    "type=file;size=1234;modify=20260930120000.123; año 2026.csv\r\n"));

                Passive(session, "RETR año 2026.csv", data => data.Write("a,b\r\n1,2\r\n"));
                Passive(session, "STOR subido.txt", data =>
                {
                    using var copy = new MemoryStream();
                    data.Reader.AsStream().CopyTo(copy);
                    uploaded = Encoding.UTF8.GetString(copy.ToArray());
                });

                session.Expect("QUIT");
                session.WriteLine("221 Adiós");
            });

            using (var client = new FtpClient())
            {
                client.Connect(server.Host, server.Port, TlsMode.None);
                client.Login("ana", "clave");
                Assert.Equal("/home/ana \"docs\"", client.GetWorkingDirectory());

                IReadOnlyList<FtpListItem> items = client.List();
                Assert.Equal(2, items.Count);
                Assert.True(items[0].IsDirectory);
                Assert.Equal(("año 2026.csv", 1234L), (items[1].Name, items[1].Size!.Value));
                Assert.Equal(new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero), items[1].Modified);

                Assert.Equal("a,b\r\n1,2\r\n", Encoding.UTF8.GetString(client.DownloadBytes("año 2026.csv")));
                client.UploadBytes(Encoding.UTF8.GetBytes("contenido nuevo"), "subido.txt");
            }

            server.Wait();
            Assert.Equal("contenido nuevo", uploaded);
        }

        [Fact]
        public void IgnoresThePrivateAddressAnnouncedByPasv()
        {
            using var server = new TestServer(session =>
            {
                session.WriteLine("220 listo");
                session.Expect("FEAT");
                session.WriteLine("500 No entiendo");
                session.Expect("USER anonymous");
                session.WriteLine("230 Bienvenido");
                session.Expect("TYPE I");
                session.WriteLine("200 Ok");

                using var listener = Listen();
                ushort port = listener.LocalAddress.Port;
                session.Expect("PASV");
                // A server behind NAT announces an address the client cannot reach.
                session.WriteLine($"227 Entering Passive Mode (10,99,99,99,{port / 256},{port % 256})");
                session.Expect("NLST");
                session.WriteLine("150 Abriendo");
                using (var data = new ServerSession(listener.Accept()))
                    data.Write("uno.txt\r\ndos.txt\r\n");
                session.WriteLine("226 Hecho");
            });

            using var client = new FtpClient();
            client.Connect(server.Host, server.Port, TlsMode.None);
            client.Login();
            Assert.Equal(["uno.txt", "dos.txt"], client.ListNames());
            server.Wait();
        }

        [Fact]
        public void ProtectsTheDataChannelWithExplicitTls()
        {
            using var server = new TestServer(session =>
            {
                session.WriteLine("220 FTPS listo");
                session.Expect("FEAT");
                session.Write("211-Features:\r\n AUTH TLS\r\n PBSZ\r\n PROT\r\n EPSV\r\n211 End\r\n");
                session.Expect("AUTH TLS");
                session.WriteLine("234 Adelante");
                session.StartTls(TestCertificate.Localhost);
                session.Expect("USER ana");
                session.WriteLine("331 Ok");
                session.Expect("PASS clave");
                session.WriteLine("230 Dentro");
                session.Expect("PBSZ 0");
                session.WriteLine("200 Ok");
                session.Expect("PROT P");
                session.WriteLine("200 Protegido");
                session.Expect("TYPE I");
                session.WriteLine("200 Ok");

                Passive(session, "RETR secreto.bin", data =>
                {
                    data.StartTls(TestCertificate.Localhost);
                    data.Write([1, 2, 3, 4]);
                });
            });

            using var client = new FtpClient(new ConnectionOptions { Tls = TestCertificate.TrustingOptions });
            client.Connect("localhost", server.Port, TlsMode.StartTls);
            Assert.True(client.IsSecure);
            client.Login("ana", "clave");

            Assert.Equal([1, 2, 3, 4], client.DownloadBytes("secreto.bin"));
            server.Wait();
        }

        [Fact]
        public void ReportsMissingFiles()
        {
            using var server = new TestServer(session =>
            {
                session.WriteLine("220 listo");
                session.Expect("FEAT");
                session.WriteLine("500 No");
                session.Expect("DELE nada.txt");
                session.WriteLine("550 No existe");
            });

            using var client = new FtpClient();
            client.Connect(server.Host, server.Port, TlsMode.None);
            var exception = Assert.Throws<FtpException>(() => client.DeleteFile("nada.txt"));
            Assert.Equal(550, exception.StatusCode);
        }

        [Fact]
        public void ParsesUnixAndDosListings()
        {
            var now = new DateTimeOffset(2026, 9, 30, 0, 0, 0, TimeSpan.Zero);

            FtpListItem file = FtpListParser.ParseList("-rw-r--r--   1 ana  staff     4096 Sep 29 18:05 notas de reunión.txt", now)!;
            Assert.Equal(("notas de reunión.txt", FtpItemType.File, 4096L), (file.Name, file.Type, file.Size!.Value));
            Assert.Equal(new DateTimeOffset(2026, 9, 29, 18, 5, 0, TimeSpan.Zero), file.Modified);

            FtpListItem december = FtpListParser.ParseList("drwxr-xr-x 2 ana staff 64 Dec 24 10:00 navidad", now)!;
            Assert.Equal(2025, december.Modified!.Value.Year); // no year and in the future: it was last year

            FtpListItem link = FtpListParser.ParseList("lrwxrwxrwx 1 root root 7 Jan  1  2024 actual -> v2.0.1", now)!;
            Assert.Equal(("actual", FtpItemType.Link), (link.Name, link.Type));

            FtpListItem dosDir = FtpListParser.ParseList("09-30-26  10:15AM       <DIR>          Informes")!;
            Assert.Equal(("Informes", FtpItemType.Directory), (dosDir.Name, dosDir.Type));

            FtpListItem dosFile = FtpListParser.ParseList("09-30-26  10:15PM              1234 datos.csv")!;
            Assert.Equal(1234, dosFile.Size);
        }

        // --- server side -------------------------------------------------------------------------------------

        private static TcpSocket Listen()
        {
            var listener = new TcpSocket();
            listener.Bind(SocketAddress.Loopback(AddressFamily.IPv4, 0));
            listener.Listen();
            return listener;
        }

        /// <summary>EPSV → command → 150 → data (scripted) → 226.</summary>
        private static void Passive(ServerSession session, string command, Action<ServerSession> data)
        {
            using var listener = Listen();
            session.Expect("EPSV");
            session.WriteLine($"229 Entering Extended Passive Mode (|||{listener.LocalAddress.Port}|)");
            session.Expect(command);
            session.WriteLine("150 Abriendo conexión de datos");
            using (var channel = new ServerSession(listener.Accept()))
                data(channel);
            session.WriteLine("226 Transferencia completa");
        }
    }
}

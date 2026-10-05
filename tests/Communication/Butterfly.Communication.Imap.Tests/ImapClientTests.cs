using System.Globalization;
using System.Text;

using Butterfly.Communication.Mail;
using Butterfly.Communication.Testing;

namespace Butterfly.Communication.Imap.Tests
{
    public class ImapClientTests
    {
        private const string Header = "From: jefe@example.com\r\nSubject: =?utf-8?B?UmV1bmnDs24=?=\r\n\r\n";
        private const string Message = "From: jefe@example.com\r\nSubject: Hola\r\n\r\nCuerpo del mensaje\r\n";

        [Fact]
        public void WorksThroughATypicalSession()
        {
            var commands = new List<string>();
            byte[]? appended = null;

            using var server = new TestServer(session =>
            {
                session.WriteLine("* OK IMAP listo");
                Command(session, commands, "* CAPABILITY IMAP4rev1 AUTH=PLAIN SASL-IR UIDPLUS MOVE");
                Command(session, commands, "", "OK [CAPABILITY IMAP4rev1 UIDPLUS MOVE] Autenticado");
                Command(session, commands,
                    "* LIST (\\HasNoChildren) \"/\" INBOX\r\n" +
                    "* LIST (\\HasNoChildren \\Sent) \"/\" \"Enviados\"\r\n" +
                    "* LIST (\\Noselect \\HasChildren) \"/\" \"Archivo &AOk-t&AOk-\"");
                Command(session, commands,
                    "* 3 EXISTS\r\n* 0 RECENT\r\n* FLAGS (\\Answered \\Flagged \\Deleted \\Seen \\Draft)\r\n" +
                    "* OK [UIDVALIDITY 3857529045] UIDs válidos\r\n* OK [UIDNEXT 4392] Próximo UID\r\n" +
                    "* OK [PERMANENTFLAGS (\\Deleted \\Seen \\*)] Límites",
                    "OK [READ-WRITE] SELECT completado");
                Command(session, commands, "* SEARCH 4390 4391");
                Command(session, commands,
                    $"* 1 FETCH (UID 4390 FLAGS (\\Seen) RFC822.SIZE 2048 INTERNALDATE \"17-Jul-2026 02:44:25 -0700\" BODY[HEADER] {{{Header.Length}}}\r\n{Header})\r\n" +
                    $"* 2 FETCH (UID 4391 FLAGS () RFC822.SIZE 99 INTERNALDATE \" 7-Jul-2026 10:00:00 +0200\" BODY[HEADER] {{{Header.Length}}}\r\n{Header})");
                Command(session, commands, $"* 2 FETCH (UID 4391 BODY[] {{{Message.Length}}}\r\n{Message})");
                Command(session, commands, "");
                Command(session, commands, "* OK [COPYUID 1 4390 1]\r\n* 1 EXPUNGE");
                appended = ReadAppend(session, commands);
                Command(session, commands, "* BYE Adiós", "OK LOGOUT completado");
            });

            using (var client = new ImapClient())
            {
                client.Connect(server.Host, server.Port, TlsMode.None);
                client.Authenticate("ana", "clave");
                Assert.Contains("MOVE", client.Capabilities);
                Assert.DoesNotContain("AUTH=PLAIN", client.Capabilities);

                IReadOnlyList<ImapFolder> folders = client.ListFolders();
                Assert.Equal(["INBOX", "Enviados", "Archivo été"], folders.Select(f => f.Name));
                Assert.Equal(@"\Sent", folders[1].SpecialUse);
                Assert.False(folders[2].CanSelect);

                ImapMailboxStatus inbox = client.Select("INBOX");
                Assert.Equal((3, 3857529045u, (uint?)4392u, false), (inbox.Exists, inbox.UidValidity, inbox.UidNext, inbox.IsReadOnly));
                Assert.Contains(@"\*", inbox.PermanentFlags);

                IReadOnlyList<uint> unseen = client.Search(ImapQuery.Unseen.And(ImapQuery.From("jefe@example.com")));
                Assert.Equal([4390u, 4391u], unseen);

                IReadOnlyList<ImapMessageSummary> summaries = client.FetchSummaries(unseen);
                Assert.Equal(2, summaries.Count);
                Assert.True(summaries[0].IsSeen);
                Assert.Equal(2048, summaries[0].Size);
                Assert.Equal(new DateTimeOffset(2026, 7, 17, 2, 44, 25, TimeSpan.FromHours(-7)), summaries[0].InternalDate);
                Assert.Equal(new DateTimeOffset(2026, 7, 7, 10, 0, 0, TimeSpan.FromHours(2)), summaries[1].InternalDate);
                Assert.Equal("Reunión", summaries[1].Headers!.Subject);

                MailMessage message = client.Fetch(4391);
                Assert.Equal("Cuerpo del mensaje\r\n", message.TextBody);

                client.MarkAsRead([4391]);
                client.Move([4390], "Archivo été");

                uint? uid = client.Append("Enviados", Encoding.ASCII.GetBytes(Message), [ImapFlags.Seen], new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero));
                Assert.Equal(77u, uid);
            }

            server.Wait();

            Assert.StartsWith("A0002 AUTHENTICATE PLAIN ", commands[1]);
            Assert.Equal("A0003 LIST \"\" \"*\"", commands[2]);
            Assert.Equal("A0004 SELECT INBOX", commands[3]);
            Assert.Equal("A0005 UID SEARCH UNSEEN FROM jefe@example.com", commands[4]);
            Assert.Equal("A0006 UID FETCH 4390:4391 (UID FLAGS RFC822.SIZE INTERNALDATE BODY.PEEK[HEADER])", commands[5]);
            Assert.Equal("A0007 UID FETCH 4391 (UID BODY.PEEK[])", commands[6]);
            Assert.Equal("A0008 UID STORE 4391 +FLAGS.SILENT (\\Seen)", commands[7]);
            Assert.Equal("A0009 UID MOVE 4390 \"Archivo &AOk-t&AOk-\"", commands[8]);
            Assert.StartsWith("A0010 APPEND Enviados (\\Seen) \"30-Sep-2026 12:00:00 +0000\" {", commands[9]);
            Assert.Equal(Message, Encoding.ASCII.GetString(appended!));
        }

        [Fact]
        public void SendsNonAsciiValuesAsLiterals()
        {
            var commands = new List<string>();
            var literals = new List<string>();
            using var server = new TestServer(session =>
            {
                session.WriteLine("* OK listo");
                Command(session, commands, "* CAPABILITY IMAP4rev1");
                literals.AddRange(CommandWithLiterals(session, commands));        // LOGIN with literal password
                Command(session, commands, "* CAPABILITY IMAP4rev1");
                Command(session, commands, "* 0 EXISTS");
                literals.AddRange(CommandWithLiterals(session, commands, "* SEARCH 9"));
            });

            using var client = new ImapClient();
            client.Connect(server.Host, server.Port, TlsMode.None);
            client.Authenticate("ana", "contraseña");
            client.Select("INBOX");
            Assert.Equal([9u], client.Search(ImapQuery.Subject("reunión")));
            server.Wait();

            Assert.Equal(["contraseña", "reunión"], literals);
            Assert.StartsWith("A0005 UID SEARCH CHARSET UTF-8 SUBJECT {", commands[4]);
        }

        [Fact]
        public void ReportsNoResponses()
        {
            using var server = new TestServer(session =>
            {
                session.WriteLine("* OK listo");
                Command(session, [], "* CAPABILITY IMAP4rev1");
                Command(session, [], "", "NO [AUTHENTICATIONFAILED] Credenciales inválidas");
            });

            using var client = new ImapClient();
            client.Connect(server.Host, server.Port, TlsMode.None);
            var exception = Assert.Throws<ImapException>(() => client.Authenticate("ana", "mala"));
            Assert.Equal("NO", exception.Status);
            Assert.Contains("AUTHENTICATIONFAILED", exception.ServerMessage);
        }

        [Theory]
        [InlineData("INBOX", "INBOX")]
        [InlineData("Archivo été", "Archivo &AOk-t&AOk-")]
        [InlineData("Tom & Jerry", "Tom &- Jerry")]
        [InlineData("日本語", "&ZeVnLIqe-")]
        public void EncodesFolderNamesInModifiedUtf7(string name, string encoded)
        {
            Assert.Equal(encoded, ImapFolderName.Encode(name));
            Assert.Equal(name, ImapFolderName.Decode(encoded));
        }

        // --- server side -------------------------------------------------------------------------------------

        /// <summary>Reads one command and answers "untagged" lines followed by the tagged status.</summary>
        private static void Command(ServerSession session, List<string> commands, string untagged, string status = "OK completado")
        {
            string command = session.ReadLine();
            commands.Add(command);
            if (untagged.Length > 0)
                session.Write(untagged + "\r\n");
            session.WriteLine($"{command.Split(' ')[0]} {status}");
        }

        /// <summary>Reads a command that carries literals ("{n}" + continuation) and returns the literal values.</summary>
        private static List<string> CommandWithLiterals(ServerSession session, List<string> commands, string untagged = "")
        {
            var literals = new List<string>();
            string line = session.ReadLine();
            string first = line;
            while (line.EndsWith('}'))
            {
                int length = int.Parse(line[(line.LastIndexOf('{') + 1)..^1], CultureInfo.InvariantCulture);
                session.WriteLine("+ Listo");
                literals.Add(Encoding.UTF8.GetString(session.ReadBytes(length)));
                line = session.ReadLine();
            }

            commands.Add(first);
            if (untagged.Length > 0)
                session.Write(untagged + "\r\n");
            session.WriteLine($"{first.Split(' ')[0]} OK completado");
            return literals;
        }

        private static byte[] ReadAppend(ServerSession session, List<string> commands)
        {
            string line = session.ReadLine();
            commands.Add(line);
            int length = int.Parse(line[(line.LastIndexOf('{') + 1)..^1], CultureInfo.InvariantCulture);
            session.WriteLine("+ Listo para el literal");
            byte[] data = session.ReadBytes(length);
            session.ReadLine();
            session.WriteLine($"{line.Split(' ')[0]} OK [APPENDUID 38505 77] APPEND completado");
            return data;
        }
    }
}

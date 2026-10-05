using System.Text;

namespace Butterfly.Communication.Mail.Tests
{
    public class MailAddressTests
    {
        [Theory]
        [InlineData("ana@example.com", "ana@example.com", null)]
        [InlineData("<ana@example.com>", "ana@example.com", null)]
        [InlineData("Ana Pérez <ana@example.com>", "ana@example.com", "Ana Pérez")]
        [InlineData("\"Pérez, Ana\" <ana@example.com>", "ana@example.com", "Pérez, Ana")]
        [InlineData("ana@example.com (Ana)", "ana@example.com", "Ana")]
        [InlineData("=?utf-8?B?w5FhbmR1?= <n@example.com>", "n@example.com", "Ñandu")]
        public void ParsesCommonForms(string text, string address, string? name)
        {
            MailAddress parsed = MailAddress.Parse(text);
            Assert.Equal(address, parsed.Address);
            Assert.Equal(name, parsed.DisplayName);
        }

        [Theory]
        [InlineData("sin-arroba")]
        [InlineData("@example.com")]
        [InlineData("ana@")]
        [InlineData("ana @example.com")]
        public void RejectsInvalidAddresses(string text) => Assert.False(MailAddress.TryParse(text, out _));

        [Fact]
        public void ParsesListsAndGroups()
        {
            var list = MailAddress.ParseList("\"Uno, A\" <a@x.com>, b@x.com; Equipo: c@x.com, d@x.com;, e@x.com");
            Assert.Equal(["a@x.com", "b@x.com", "c@x.com", "d@x.com", "e@x.com"], list.Select(a => a.Address));
        }

        [Fact]
        public void EncodesNonAsciiDisplayNames()
        {
            var address = new MailAddress("jose@example.com", "José Muñoz");
            Assert.Equal("=?utf-8?B?Sm9zw6kgTXXDsW96?= <jose@example.com>", address.ToHeaderString());
            Assert.Equal("\"Doe, John\" <j@x.com>", new MailAddress("j@x.com", "Doe, John").ToHeaderString());
        }
    }

    public class MimeEncodingTests
    {
        [Theory]
        [InlineData("=?ISO-8859-1?Q?Andr=E9?= Pirard", "André Pirard")]
        [InlineData("=?utf-8?B?SG9sYQ==?= =?utf-8?B?IG11bmRv?=", "Hola mundo")]
        [InlineData("=?utf-8?q?caf=C3=A9_con_leche?=", "café con leche")]
        [InlineData("texto normal", "texto normal")]
        public void DecodesEncodedWords(string encoded, string expected) => Assert.Equal(expected, MimeEncoding.DecodeHeaderText(encoded));

        [Fact]
        public void EncodedWordsRoundTripAndStayShort()
        {
            string subject = "Informe trimestral — ventas, márgenes y previsión 2026 🦋 ".PadRight(120, 'ñ');
            string encoded = MimeEncoding.EncodeHeaderText(subject);

            Assert.All(encoded.Split(' '), word => Assert.True(word.Length <= 75, word));
            Assert.Equal(subject, MimeEncoding.DecodeHeaderText(encoded));
        }

        [Fact]
        public void QuotedPrintableRoundTrip()
        {
            string text = "Línea con = y espacio final \r\n" + new string('x', 200) + "\r\núltima";
            string encoded = MimeEncoding.EncodeQuotedPrintable(Encoding.UTF8.GetBytes(text));

            Assert.All(encoded.Split("\r\n"), line => Assert.True(line.Length <= 76, line));
            Assert.Contains("=3D", encoded);
            Assert.Contains("final=20\r\n", encoded);
            Assert.Equal(text, Encoding.UTF8.GetString(MimeEncoding.DecodeQuotedPrintable(encoded)));
        }

        [Fact]
        public void DecodesRfc2231Parameters()
        {
            var disposition = ContentDisposition.Parse("attachment; filename*0*=utf-8''%E2%82%AC%20rates; filename*1=\".pdf\"");
            Assert.True(disposition.IsAttachment);
            Assert.Equal("€ rates.pdf", disposition.FileName);

            var type = ContentType.Parse("text/plain; CHARSET=\"ISO-8859-1\"; name*=iso-8859-1'es'Se%F1or.txt");
            Assert.Equal("text/plain", type.MediaType);
            Assert.Equal("ISO-8859-1", type.Charset);
            Assert.Equal("Señor.txt", type.Name);
        }

        [Fact]
        public void FoldsLongHeaders()
        {
            string folded = MimeEncoding.FoldHeader("To", string.Join(", ", Enumerable.Range(0, 12).Select(i => $"persona{i}@example.com")));
            Assert.All(folded.Split("\r\n"), line => Assert.True(line.Length <= 78, line));
            Assert.All(folded.Split("\r\n").Skip(1), line => Assert.StartsWith(" ", line));
        }

        [Theory]
        [InlineData("Tue, 1 Jul 2003 10:52:37 +0200", 2003, 7, 1, 10, 2)]
        [InlineData("1 Jul 03 10:52 GMT", 2003, 7, 1, 10, 0)]
        [InlineData("Mon, 29 Sep 2026 22:00:00 -0500 (EST)", 2026, 9, 29, 22, -5)]
        public void ParsesDates(string text, int year, int month, int day, int hour, int offsetHours)
        {
            Assert.True(MimeDate.TryParse(text, out DateTimeOffset date));
            Assert.Equal(new DateTimeOffset(year, month, day, hour, date.Minute, date.Second, TimeSpan.FromHours(offsetHours)), date);
        }

        [Fact]
        public void FormatsDates()
            => Assert.Equal("Tue, 01 Jul 2003 10:52:37 +0200", MimeDate.Format(new DateTimeOffset(2003, 7, 1, 10, 52, 37, TimeSpan.FromHours(2))));
    }

    public class MailMessageTests
    {
        [Fact]
        public void RoundTripsACompleteMessage()
        {
            byte[] pdf = [0x25, 0x50, 0x44, 0x46, 0x00, 0xFF, 0x10];
            byte[] logo = [0x89, 0x50, 0x4E, 0x47];

            var message = new MailMessage
            {
                From = new MailAddress("ventas@example.com", "Equipo de Ventas"),
                Subject = "Presupuesto nº 42 — revisión",
                TextBody = "Hola,\nadjunto el presupuesto.\n\nSaludos",
                HtmlBody = "<p>Hola,</p><img src=\"cid:logo\"><p>adjunto el presupuesto.</p>",
            };
            message.To.Add("Cliente Ñúñez <cliente@example.org>");
            message.Cc.Add("copia@example.org");
            message.Bcc.Add("oculto@example.org");
            message.Attachments.Add(new MailAttachment("presupuesto año 2026.pdf", pdf));
            message.Attachments.Add(MailAttachment.Inline("logo", "logo.png", logo));
            message.Headers.Add("X-Priority", "1");

            byte[] raw = message.ToBytes();
            string text = Encoding.ASCII.GetString(raw);

            Assert.True(Ascii.IsValid(raw));
            Assert.DoesNotContain("oculto@example.org", text);
            Assert.All(text.Split("\r\n"), line => Assert.True(line.Length <= 998));
            Assert.Contains("multipart/mixed", text);
            Assert.Contains("multipart/alternative", text);
            Assert.Contains("multipart/related", text);

            MailMessage parsed = MailMessage.Parse(raw);

            Assert.Equal("Equipo de Ventas", parsed.From!.DisplayName);
            Assert.Equal("Presupuesto nº 42 — revisión", parsed.Subject);
            Assert.Equal("Cliente Ñúñez", Assert.Single(parsed.To).DisplayName);
            Assert.Equal("copia@example.org", Assert.Single(parsed.Cc).Address);
            Assert.Empty(parsed.Bcc);
            Assert.Equal("Hola,\r\nadjunto el presupuesto.\r\n\r\nSaludos", parsed.TextBody);
            Assert.Equal(message.HtmlBody, parsed.HtmlBody);
            Assert.Equal("1", parsed.Headers["X-Priority"]);
            Assert.NotNull(parsed.Date);

            MailAttachment attachment = Assert.Single(parsed.Attachments, a => !a.IsInline);
            Assert.Equal("presupuesto año 2026.pdf", attachment.FileName);
            Assert.Equal("application/pdf", attachment.ContentType);
            Assert.Equal(pdf, attachment.Content);

            MailAttachment inline = Assert.Single(parsed.Attachments, a => a.IsInline);
            Assert.Equal("logo", inline.ContentId);
            Assert.Equal(logo, inline.Content);
        }

        [Fact]
        public void ParsesLenientRealWorldMessages()
        {
            // LF line endings, folded headers, a Latin-1 quoted-printable body and a name only in Content-Type.
            string raw =
                "From: =?ISO-8859-1?Q?Jos=E9?= <jose@example.com>\n" +
                "To: a@example.com,\n b@example.com\n" +
                "Subject: Prueba\n" +
                "Content-Type: multipart/mixed; boundary=XYZ\n" +
                "\n" +
                "preámbulo ignorado\n" +
                "--XYZ\n" +
                "Content-Type: text/plain; charset=iso-8859-1\n" +
                "Content-Transfer-Encoding: quoted-printable\n" +
                "\n" +
                "Ma=F1ana a las 10=\n" +
                ":00.\n" +
                "--XYZ\n" +
                "Content-Type: application/octet-stream; name=\"datos.bin\"\n" +
                "Content-Transfer-Encoding: base64\n" +
                "\n" +
                "AQID\n" +
                "BA==\n" +
                "--XYZ--\n";

            MailMessage message = MailMessage.Parse(Encoding.Latin1.GetBytes(raw));

            Assert.Equal("José", message.From!.DisplayName);
            Assert.Equal(["a@example.com", "b@example.com"], message.To.Select(a => a.Address));
            Assert.Equal("Mañana a las 10:00.", message.TextBody);
            MailAttachment attachment = Assert.Single(message.Attachments);
            Assert.Equal("datos.bin", attachment.FileName);
            Assert.Equal([1, 2, 3, 4], attachment.Content);
        }

        [Fact]
        public void PlainAsciiTextStaysReadable()
        {
            var message = new MailMessage { From = "a@b.c", Subject = "Hola", TextBody = "Linea 1\nLinea 2" };
            message.To.Add("d@e.f");
            string text = Encoding.ASCII.GetString(message.ToBytes());

            Assert.Contains("Content-Transfer-Encoding: 7bit\r\n\r\nLinea 1\r\nLinea 2", text);
        }
    }
}

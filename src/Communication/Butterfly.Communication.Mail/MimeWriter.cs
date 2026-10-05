using System.Security.Cryptography;
using System.Text;

namespace Butterfly.Communication.Mail
{
    /// <summary>
    /// Writes a <see cref="MailMessage"/> as MIME. Structure: text and HTML become multipart/alternative, inline
    /// resources wrap the HTML in multipart/related, and attachments wrap everything in multipart/mixed.
    /// </summary>
    internal static class MimeWriter
    {
        public static byte[] Write(MailMessage message)
        {
            var output = new StringBuilder(4096);

            DateTimeOffset date = message.Date ?? DateTimeOffset.Now;
            string messageId = message.MessageId ?? $"{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16))}@{message.From?.Domain ?? "butterfly.local"}";

            Header(output, "Date", MimeDate.Format(date));
            if (message.From is { } from)
                Header(output, "From", from.ToHeaderString());
            if (message.Sender is { } sender)
                Header(output, "Sender", sender.ToHeaderString());
            if (message.ReplyTo.Count > 0)
                Header(output, "Reply-To", message.ReplyTo.ToHeaderString());
            if (message.To.Count > 0)
                Header(output, "To", message.To.ToHeaderString());
            if (message.Cc.Count > 0)
                Header(output, "Cc", message.Cc.ToHeaderString());
            if (message.Subject is not null)
                Header(output, "Subject", MimeEncoding.EncodeHeaderText(message.Subject));
            Header(output, "Message-ID", $"<{messageId}>");
            if (message.InReplyTo is not null)
                Header(output, "In-Reply-To", $"<{message.InReplyTo}>");

            foreach (var (name, value) in message.Headers)
                Header(output, name, MimeEncoding.IsAscii(value) ? value : MimeEncoding.EncodeHeaderText(value));

            Header(output, "MIME-Version", "1.0");
            WriteBody(output, message);

            // Everything above is ASCII by construction (headers are encoded, bodies use QP or base64).
            return Encoding.ASCII.GetBytes(output.ToString());
        }

        private static void WriteBody(StringBuilder output, MailMessage message)
        {
            var inline = message.Attachments.Where(a => a.IsInline).ToList();
            var attached = message.Attachments.Where(a => !a.IsInline).ToList();

            Action<StringBuilder> body = (message.TextBody, message.HtmlBody) switch
            {
                (not null, not null) => o => Multipart(o, "alternative", [p => TextPart(p, message.TextBody, "plain"), p => HtmlWithResources(p, message.HtmlBody, inline)]),
                (null, not null) => o => HtmlWithResources(o, message.HtmlBody, inline),
                _ => o => TextPart(o, message.TextBody ?? "", "plain"),
            };

            if (attached.Count == 0)
            {
                body(output);
                return;
            }

            Multipart(output, "mixed", [body, .. attached.Select<MailAttachment, Action<StringBuilder>>(a => o => AttachmentPart(o, a))]);
        }

        private static void HtmlWithResources(StringBuilder output, string html, List<MailAttachment> resources)
        {
            if (resources.Count == 0)
            {
                TextPart(output, html, "html");
                return;
            }

            Multipart(output, "related", [o => TextPart(o, html, "html"), .. resources.Select<MailAttachment, Action<StringBuilder>>(r => o => AttachmentPart(o, r))]);
        }

        private static void Multipart(StringBuilder output, string subtype, IReadOnlyList<Action<StringBuilder>> parts)
        {
            // "=_" can appear neither in base64 nor in quoted-printable output, so the boundary cannot collide.
            string boundary = "=_butterfly_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));
            Header(output, "Content-Type", $"multipart/{subtype}; boundary=\"{boundary}\"");
            output.Append("\r\n");

            foreach (var part in parts)
            {
                output.Append("--").Append(boundary).Append("\r\n");
                part(output);
                output.Append("\r\n");
            }

            output.Append("--").Append(boundary).Append("--\r\n");
        }

        private static void TextPart(StringBuilder output, string text, string subtype)
        {
            // Normalize every line ending to CRLF, the only one allowed on the wire.
            string normalized = text.ReplaceLineEndings("\r\n");
            byte[] bytes = Encoding.UTF8.GetBytes(normalized);
            bool plain = MimeEncoding.IsAscii(normalized) && normalized.Split("\r\n").All(l => l.Length <= 998);

            Header(output, "Content-Type", $"text/{subtype}; charset=utf-8");
            Header(output, "Content-Transfer-Encoding", plain ? "7bit" : "quoted-printable");
            output.Append("\r\n");
            output.Append(plain ? normalized : MimeEncoding.EncodeQuotedPrintable(bytes));
        }

        private static void AttachmentPart(StringBuilder output, MailAttachment attachment)
        {
            Header(output, "Content-Type", $"{attachment.ContentType}; {MimeEncoding.FormatParameter("name", attachment.FileName)}");
            Header(output, "Content-Transfer-Encoding", "base64");
            Header(output, "Content-Disposition", $"{(attachment.IsInline ? "inline" : "attachment")}; {MimeEncoding.FormatParameter("filename", attachment.FileName)}");
            if (attachment.ContentId is { } id)
                Header(output, "Content-ID", $"<{id}>");
            output.Append("\r\n");
            output.Append(MimeEncoding.EncodeBase64Lines(attachment.Content));
        }

        private static void Header(StringBuilder output, string name, string value)
            => output.Append(MimeEncoding.FoldHeader(name, value)).Append("\r\n");
    }
}

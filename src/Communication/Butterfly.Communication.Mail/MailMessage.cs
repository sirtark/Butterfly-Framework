namespace Butterfly.Communication.Mail
{
    public sealed class MailAttachment
    {
        private static readonly Dictionary<string, string> s_mediaTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            [".txt"] = "text/plain", [".csv"] = "text/csv", [".html"] = "text/html", [".htm"] = "text/html",
            [".xml"] = "application/xml", [".json"] = "application/json", [".pdf"] = "application/pdf",
            [".zip"] = "application/zip", [".gz"] = "application/gzip", [".png"] = "image/png",
            [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".gif"] = "image/gif", [".svg"] = "image/svg+xml",
            [".webp"] = "image/webp", [".ics"] = "text/calendar", [".eml"] = "message/rfc822",
            [".doc"] = "application/msword", [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            [".xls"] = "application/vnd.ms-excel", [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        };

        public MailAttachment(string fileName, byte[] content, string? contentType = null)
        {
            FileName = fileName;
            Content = content;
            ContentType = contentType ?? GuessMediaType(fileName);
        }

        public string FileName { get; }
        public byte[] Content { get; }
        public string ContentType { get; }

        /// <summary>Set to reference the part from HTML (<c>&lt;img src="cid:logo"&gt;</c>): it is sent inline.</summary>
        public string? ContentId { get; init; }

        public bool IsInline => ContentId is not null;

        public static MailAttachment FromFile(string path, string? contentType = null)
            => new(Path.GetFileName(path), File.ReadAllBytes(path), contentType);

        public static MailAttachment Inline(string contentId, string fileName, byte[] content, string? contentType = null)
            => new(fileName, content, contentType) { ContentId = contentId };

        public static string GuessMediaType(string fileName)
            => s_mediaTypes.GetValueOrDefault(Path.GetExtension(fileName), "application/octet-stream");
    }

    /// <summary>An e-mail message: build one to send it, or get one parsed from POP3/IMAP.</summary>
    public sealed class MailMessage
    {
        public MailAddress? From { get; set; }

        /// <summary>The actual sender when it differs from <see cref="From"/> (a secretary sending for a manager).</summary>
        public MailAddress? Sender { get; set; }

        public MailAddressCollection To { get; } = [];
        public MailAddressCollection Cc { get; } = [];

        /// <summary>Recipients that receive the message without appearing in its headers.</summary>
        public MailAddressCollection Bcc { get; } = [];

        public MailAddressCollection ReplyTo { get; } = [];
        public string? Subject { get; set; }
        public string? TextBody { get; set; }
        public string? HtmlBody { get; set; }
        public List<MailAttachment> Attachments { get; } = [];

        /// <summary>Set when sending if left empty.</summary>
        public DateTimeOffset? Date { get; set; }

        /// <summary>Generated when sending if left empty; without angle brackets.</summary>
        public string? MessageId { get; set; }

        public string? InReplyTo { get; set; }

        /// <summary>Additional headers (X-Mailer, List-Unsubscribe...). Standard headers are generated from the properties.</summary>
        public MimeHeaders Headers { get; } = new();

        /// <summary>The MIME structure the message was parsed from; null for messages built in code.</summary>
        public MimeEntity? Mime { get; private set; }

        /// <summary>Every recipient of the envelope: To, Cc and Bcc without duplicates.</summary>
        public IEnumerable<MailAddress> AllRecipients => To.Concat(Cc).Concat(Bcc).Distinct();

        /// <summary>The message in RFC 5322 format with CRLF line endings. Bcc is never written.</summary>
        public byte[] ToBytes() => MimeWriter.Write(this);

        public void WriteTo(Stream stream) => stream.Write(ToBytes());

        public static MailMessage Parse(ReadOnlySpan<byte> data) => FromMime(MimeEntity.Parse(data));

        public static MailMessage Load(Stream stream)
        {
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return Parse(memory.GetBuffer().AsSpan(0, (int)memory.Length));
        }

        public static MailMessage FromMime(MimeEntity entity)
        {
            MimeHeaders headers = entity.Headers;
            var message = new MailMessage
            {
                Mime = entity,
                From = MailAddress.ParseList(headers.Get("From")).FirstOrDefault(),
                Sender = MailAddress.ParseList(headers.Get("Sender")).FirstOrDefault(),
                Subject = headers.Get("Subject") is { } subject ? MimeEncoding.DecodeHeaderText(subject) : null,
                MessageId = headers.Get("Message-ID")?.Trim().Trim('<', '>'),
                InReplyTo = headers.Get("In-Reply-To")?.Trim().Trim('<', '>'),
                Date = MimeDate.TryParse(headers.Get("Date"), out DateTimeOffset date) ? date : null,
            };

            AddAll(message.To, headers.GetValues("To"));
            AddAll(message.Cc, headers.GetValues("Cc"));
            AddAll(message.Bcc, headers.GetValues("Bcc"));
            AddAll(message.ReplyTo, headers.GetValues("Reply-To"));

            foreach (var (name, value) in headers)
            {
                if (!s_standardHeaders.Contains(name))
                    message.Headers.AddRaw(name, value);
            }

            foreach (MimeEntity part in entity.Descendants().Where(p => !p.IsMultipart))
            {
                if (part.IsAttachment)
                {
                    message.Attachments.Add(new MailAttachment(part.FileName ?? "attachment", part.Content, part.ContentType.MediaType));
                }
                else if (part.ContentType.Is("text/plain") && message.TextBody is null)
                {
                    message.TextBody = part.GetText();
                }
                else if (part.ContentType.Is("text/html") && message.HtmlBody is null)
                {
                    message.HtmlBody = part.GetText();
                }
                else if (part.ContentType.TopLevelType != "text" || part.ContentId is not null)
                {
                    // Inline images and other embedded resources.
                    message.Attachments.Add(new MailAttachment(part.FileName ?? part.ContentId ?? "inline", part.Content, part.ContentType.MediaType)
                    {
                        ContentId = part.ContentId,
                    });
                }
            }

            return message;
        }

        private static readonly HashSet<string> s_standardHeaders = new(StringComparer.OrdinalIgnoreCase)
        {
            "From", "Sender", "To", "Cc", "Bcc", "Reply-To", "Subject", "Date", "Message-ID", "In-Reply-To",
            "MIME-Version", "Content-Type", "Content-Transfer-Encoding", "Content-Disposition",
        };

        private static void AddAll(MailAddressCollection target, IReadOnlyList<string> values)
        {
            foreach (string value in values)
            {
                foreach (MailAddress address in MailAddress.ParseList(value))
                    target.Add(address);
            }
        }
    }
}

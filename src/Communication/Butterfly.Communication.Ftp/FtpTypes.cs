using System.Globalization;
using System.Text.RegularExpressions;

namespace Butterfly.Communication.Ftp
{
    public sealed record FtpResponse(int Code, IReadOnlyList<string> Lines)
    {
        public string Message => string.Join(" ", Lines);

        public bool IsPositive => Code is >= 100 and < 400;

        public override string ToString() => $"{Code} {Message}";
    }

    public class FtpException : CommunicationException
    {
        public FtpException(string message, FtpResponse? response = null)
            : base(response is null ? message : $"{message} Server said: {response}") => Response = response;

        public FtpResponse? Response { get; }
        public int? StatusCode => Response?.Code;
    }

    public enum FtpItemType
    {
        File,
        Directory,
        Link,
        Unknown
    }

    public sealed record FtpListItem(string Name, FtpItemType Type, long? Size, DateTimeOffset? Modified, string? Permissions, string Raw)
    {
        public bool IsDirectory => Type == FtpItemType.Directory;
    }

    /// <summary>Parses directory listings: MLSD facts (RFC 3659) and the classic Unix and DOS/IIS LIST formats.</summary>
    public static partial class FtpListParser
    {
        // drwxr-xr-x   2 owner group   4096 Jan 10 12:34 name   |   -rw-r--r-- 1 owner group 123 Jan 10  2024 name
        [GeneratedRegex(@"^([-dlbcps])([-rwxsStT]{9})\S*\s+\d+\s+\S+\s+\S+\s+(\d+)\s+([A-Za-z]{3}\s+\d{1,2}\s+(?:\d{1,2}:\d{2}|\d{4}))\s+(.+)$", RegexOptions.CultureInvariant)]
        private static partial Regex Unix();

        // 01-10-24  12:34PM       <DIR>          folder   |   01-10-2024  12:34       1234 file.txt
        [GeneratedRegex(@"^(\d{2}-\d{2}-\d{2,4})\s+(\d{1,2}:\d{2}(?:[AP]M)?)\s+(<DIR>|\d+)\s+(.+)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
        private static partial Regex Dos();

        public static FtpListItem? ParseMlsd(string line)
        {
            int space = line.IndexOf(' ');
            if (space < 0)
                return null;

            string name = line[(space + 1)..];
            var facts = line[..space].Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(f => f.Split('=', 2))
                .Where(f => f.Length == 2)
                .ToDictionary(f => f[0].ToLowerInvariant(), f => f[1], StringComparer.OrdinalIgnoreCase);

            string type = facts.GetValueOrDefault("type", "").ToLowerInvariant();

            // "cdir" and "pdir" are the listed directory itself and its parent.
            if (type is "cdir" or "pdir")
                return null;

            return new FtpListItem(name,
                type switch { "file" => FtpItemType.File, "dir" => FtpItemType.Directory, _ when type.StartsWith("os.unix=symlink") => FtpItemType.Link, _ => FtpItemType.Unknown },
                long.TryParse(facts.GetValueOrDefault("size"), NumberStyles.None, CultureInfo.InvariantCulture, out long size) ? size : null,
                ParseTimeVal(facts.GetValueOrDefault("modify")),
                facts.GetValueOrDefault("perm") ?? facts.GetValueOrDefault("unix.mode"),
                line);
        }

        public static FtpListItem? ParseList(string line, DateTimeOffset? now = null)
        {
            Match unix = Unix().Match(line);
            if (unix.Success)
            {
                string name = unix.Groups[5].Value;
                FtpItemType type = unix.Groups[1].Value switch { "d" => FtpItemType.Directory, "l" => FtpItemType.Link, "-" => FtpItemType.File, _ => FtpItemType.Unknown };
                if (type == FtpItemType.Link && name.IndexOf(" -> ", StringComparison.Ordinal) is var arrow and > 0)
                    name = name[..arrow];

                return new FtpListItem(name, type, long.Parse(unix.Groups[3].Value, CultureInfo.InvariantCulture),
                    ParseUnixDate(unix.Groups[4].Value, now ?? DateTimeOffset.UtcNow), unix.Groups[1].Value + unix.Groups[2].Value, line);
            }

            Match dos = Dos().Match(line);
            if (dos.Success)
            {
                bool directory = dos.Groups[3].Value.Equals("<DIR>", StringComparison.OrdinalIgnoreCase);
                DateTimeOffset? modified = DateTime.TryParse($"{dos.Groups[1].Value} {dos.Groups[2].Value}", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime date) ? new DateTimeOffset(date, TimeSpan.Zero) : null;

                return new FtpListItem(dos.Groups[4].Value, directory ? FtpItemType.Directory : FtpItemType.File,
                    directory ? null : long.Parse(dos.Groups[3].Value, CultureInfo.InvariantCulture), modified, null, line);
            }

            return null;
        }

        /// <summary>MDTM / MLSD time: YYYYMMDDHHMMSS[.sss] in UTC.</summary>
        public static DateTimeOffset? ParseTimeVal(string? value)
        {
            if (value is null || value.Length < 14)
                return null;

            return DateTime.TryParseExact(value[..14], "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime date)
                ? new DateTimeOffset(date, TimeSpan.Zero)
                : null;
        }

        private static DateTimeOffset? ParseUnixDate(string text, DateTimeOffset now)
        {
            string[] parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3)
                return null;

            // "Jan 10 12:34" has no year: it is within the last six months, so pick the year that makes it past.
            if (parts[2].Contains(':'))
            {
                if (!DateTime.TryParseExact($"{parts[0]} {parts[1]} {now.Year} {parts[2]}", "MMM d yyyy H:mm", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime recent))
                    return null;

                var date = new DateTimeOffset(recent, TimeSpan.Zero);
                return date > now.AddDays(1) ? date.AddYears(-1) : date;
            }

            return DateTime.TryParseExact($"{parts[0]} {parts[1]} {parts[2]}", "MMM d yyyy", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime old)
                ? new DateTimeOffset(old, TimeSpan.Zero)
                : null;
        }
    }
}

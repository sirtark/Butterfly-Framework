using System.Globalization;
using System.Text.RegularExpressions;

namespace Butterfly.Communication.Mail
{
    /// <summary>RFC 5322 dates ("Tue, 01 Jul 2025 10:52:37 +0200"), including the obsolete zone names.</summary>
    public static partial class MimeDate
    {
        private static readonly Dictionary<string, int> s_zones = new(StringComparer.OrdinalIgnoreCase)
        {
            ["UT"] = 0, ["UTC"] = 0, ["GMT"] = 0, ["Z"] = 0,
            ["EST"] = -5, ["EDT"] = -4, ["CST"] = -6, ["CDT"] = -5,
            ["MST"] = -7, ["MDT"] = -6, ["PST"] = -8, ["PDT"] = -7,
        };

        [GeneratedRegex(@"^\s*(?:[A-Za-z]{3},\s*)?(\d{1,2})\s+([A-Za-z]{3})\s+(\d{2,4})\s+(\d{1,2}):(\d{2})(?::(\d{2}))?\s*([+-]\d{4}|[A-Za-z]{1,5})?", RegexOptions.CultureInvariant)]
        private static partial Regex DatePattern();

        public static string Format(DateTimeOffset date)
        {
            TimeSpan offset = date.Offset;
            string sign = offset < TimeSpan.Zero ? "-" : "+";
            offset = offset.Duration();
            return date.ToString("ddd, dd MMM yyyy HH:mm:ss ", CultureInfo.InvariantCulture) + $"{sign}{offset.Hours:00}{offset.Minutes:00}";
        }

        public static bool TryParse(string? text, out DateTimeOffset date)
        {
            date = default;
            if (text is null)
                return false;

            // Comments such as "(CET)" carry no information.
            Match match = DatePattern().Match(Regex.Replace(text, @"\([^)]*\)", " "));
            if (!match.Success)
                return false;

            int month = Array.FindIndex(CultureInfo.InvariantCulture.DateTimeFormat.AbbreviatedMonthNames,
                m => m.Equals(match.Groups[2].Value, StringComparison.OrdinalIgnoreCase)) + 1;
            if (month == 0)
                return false;

            int year = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
            if (match.Groups[3].Value.Length == 2)
                year += year < 50 ? 2000 : 1900; // RFC 5322 4.3

            TimeSpan offset = TimeSpan.Zero;
            string zone = match.Groups[7].Value;
            if (zone.Length == 5 && zone[0] is '+' or '-')
            {
                int hours = int.Parse(zone.AsSpan(1, 2), CultureInfo.InvariantCulture);
                int minutes = int.Parse(zone.AsSpan(3, 2), CultureInfo.InvariantCulture);
                offset = new TimeSpan(hours, minutes, 0) * (zone[0] == '-' ? -1 : 1);
            }
            else if (s_zones.TryGetValue(zone, out int zoneHours))
            {
                offset = TimeSpan.FromHours(zoneHours);
            }

            try
            {
                date = new DateTimeOffset(year, month, int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                    int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups[5].Value, CultureInfo.InvariantCulture),
                    match.Groups[6].Success ? int.Parse(match.Groups[6].Value, CultureInfo.InvariantCulture) : 0, offset);
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
        }
    }
}

using System.Text;

namespace Butterfly.Communication
{
    /// <summary>Charset names as found in Content-Type headers and encoded words, including legacy code pages.</summary>
    public static class Charsets
    {
        static Charsets() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        /// <summary>
        /// The encoding named <paramref name="charset"/>, or <paramref name="fallback"/> (UTF-8 by default) when the
        /// name is missing or unknown. Latin-1 mislabelled as us-ascii is common, so ASCII decodes as Latin-1.
        /// </summary>
        public static Encoding GetEncoding(string? charset, Encoding? fallback = null)
        {
            fallback ??= Encoding.UTF8;
            if (string.IsNullOrWhiteSpace(charset))
                return fallback;

            string name = charset.Trim().Trim('"');
            if (name.Equals("us-ascii", StringComparison.OrdinalIgnoreCase) || name.Equals("ascii", StringComparison.OrdinalIgnoreCase))
                return Encoding.Latin1;

            try
            {
                return Encoding.GetEncoding(name);
            }
            catch (ArgumentException)
            {
                return fallback;
            }
        }
    }
}

namespace Butterfly.SystemInfo.Native
{
    // Reads the small text files Linux exposes under /proc and /sys. A missing or unreadable file is not an error:
    // the information is simply not available (another kernel, a container, restricted permissions).
    internal static class SystemFiles
    {
        private static readonly char[] Blank = [' ', '\t', '\r', '\n', '\0'];

        public static string? ReadText(string path)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        // Single-value files (/sys attributes, device-tree strings end with NUL).
        public static string? ReadLine(string path) => ReadText(path)?.Trim(Blank) is { Length: > 0 } line ? line : null;
    }
}

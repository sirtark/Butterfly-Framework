using Microsoft.Win32;
using System.Runtime.Versioning;
using System.Security;

namespace Butterfly.SystemInfo.Native
{
    // Read-only HKEY_LOCAL_MACHINE lookups. A missing key or value, or one the process cannot read, yields null.
    [SupportedOSPlatform("windows")]
    internal static class WindowsRegistry
    {
        public static object? GetValue(string key, string name)
        {
            try
            {
                using var subKey = Registry.LocalMachine.OpenSubKey(key);
                return subKey?.GetValue(name);
            }
            catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException or IOException)
            {
                return null;
            }
        }

        public static string? GetString(string key, string name) => (GetValue(key, name) as string)?.Trim() is { Length: > 0 } text ? text : null;

        public static int? GetInt32(string key, string name) => GetValue(key, name) is int value ? value : null;
    }
}

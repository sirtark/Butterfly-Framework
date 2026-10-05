using Butterfly.SystemInfo.Native;
using System.Globalization;
using System.Runtime.Versioning;

namespace Butterfly.SystemInfo.OS.Detection
{
    internal static class OSDetector
    {
        public static OSInfoSnapshot Detect()
        {
            if (OperatingSystem.IsWindows())
                return Windows();
            if (OperatingSystem.IsLinux())
                return LinuxOS.Detect();
            if (OperatingSystem.IsMacOS())
                return MacOS();
            if (OperatingSystem.IsFreeBSD())
                return FreeBSD();

            return new OSInfoSnapshot { Is64Bit = Environment.Is64BitOperatingSystem };
        }

        // Official .NET container images set it; it is the only hint that works the same everywhere.
        internal static bool RunningInContainerVariable() =>
            string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase);

        [SupportedOSPlatform("windows")]
        private static OSInfoSnapshot Windows()
        {
            const string CurrentVersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
            const string ControlKey = @"SYSTEM\CurrentControlSet\Control";

            var version = Environment.OSVersion.Version;
            if (WindowsRegistry.GetInt32(CurrentVersionKey, "UBR") is int revision and >= 0)
                version = new Version(version.Major, version.Minor, version.Build, revision);

            return new OSInfoSnapshot
            {
                Family = OSFamily.Windows,
                KernelFamily = OSKernelFamily.WindowsNT,
                Name = WindowsProductName(WindowsRegistry.GetString(CurrentVersionKey, "ProductName"), version.Build),
                Version = version,
                DisplayVersion = WindowsRegistry.GetString(CurrentVersionKey, "DisplayVersion") ?? WindowsRegistry.GetString(CurrentVersionKey, "ReleaseId"),
                KernelVersion = version.ToString(),
                Is64Bit = Environment.Is64BitOperatingSystem,
                IsContainer = WindowsRegistry.GetValue(ControlKey, "ContainerType") is not null || RunningInContainerVariable()
            };
        }

        // Windows 11 kept "Windows 10" in ProductName for compatibility; build 22000 is the first Windows 11.
        internal static string? WindowsProductName(string? productName, int build) =>
            productName is not null && build >= 22000 && productName.StartsWith("Windows 10", StringComparison.Ordinal)
                ? "Windows 11" + productName["Windows 10".Length..]
                : productName;

        private static OSInfoSnapshot MacOS()
        {
            var productVersion = Sysctl.GetString("kern.osproductversion");
            return new OSInfoSnapshot
            {
                Family = OSFamily.MacOS,
                KernelFamily = OSKernelFamily.XNU,
                Name = "macOS",
                Version = ParseVersion(productVersion) ?? Environment.OSVersion.Version,
                DisplayVersion = productVersion,
                KernelVersion = Sysctl.GetString("kern.osrelease"),
                Is64Bit = Environment.Is64BitOperatingSystem
            };
        }

        private static OSInfoSnapshot FreeBSD()
        {
            var release = Sysctl.GetString("kern.osrelease");
            return new OSInfoSnapshot
            {
                Family = OSFamily.FreeBSD,
                KernelFamily = OSKernelFamily.FreeBSD,
                Name = "FreeBSD",
                Version = ParseVersion(release),
                DisplayVersion = release,
                KernelVersion = release,
                Is64Bit = Environment.Is64BitOperatingSystem,
                IsContainer = Sysctl.GetInt64("security.jail.jailed") == 1 || RunningInContainerVariable()
            };
        }

        // Leading numeric part of a version: "12" -> 12.0, "24.04" -> 24.4, "14.1-RELEASE-p3" -> 14.1, "3.20.3" -> 3.20.3.
        internal static Version? ParseVersion(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            var length = 0;
            while (length < text.Length && (char.IsAsciiDigit(text[length]) || text[length] == '.'))
                length++;

            var parts = text[..length].Split('.', StringSplitOptions.RemoveEmptyEntries).Take(4)
                .Select(part => int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : -1)
                .ToArray();

            return parts switch
            {
                [] => null,
                _ when parts.Any(part => part < 0) => null,
                [var major] => new Version(major, 0),
                [var major, var minor] => new Version(major, minor),
                [var major, var minor, var build] => new Version(major, minor, build),
                [var major, var minor, var build, var revision, ..] => new Version(major, minor, build, revision)
            };
        }
    }
}

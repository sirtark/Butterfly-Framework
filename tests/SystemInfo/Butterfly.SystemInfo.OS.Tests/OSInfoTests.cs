using Butterfly.SystemInfo.OS.Detection;

namespace Butterfly.SystemInfo.OS.Tests
{
    public class OSInfoTests
    {
        [Fact]
        public void DetectsTheRunningOperatingSystem()
        {
            var os = OSInfoSnapshotProvider.Get();

            var (family, kernel) =
                OperatingSystem.IsWindows() ? (OSFamily.Windows, OSKernelFamily.WindowsNT) :
                OperatingSystem.IsLinux() ? (OSFamily.Linux, OSKernelFamily.Linux) :
                OperatingSystem.IsMacOS() ? (OSFamily.MacOS, OSKernelFamily.XNU) :
                OperatingSystem.IsFreeBSD() ? (OSFamily.FreeBSD, OSKernelFamily.FreeBSD) :
                (OSFamily.Unknown, OSKernelFamily.Unknown);

            Assert.Equal(family, os.Family);
            Assert.Equal(kernel, os.KernelFamily);
            Assert.Equal(Environment.Is64BitOperatingSystem, os.Is64Bit);
            Assert.False(string.IsNullOrWhiteSpace(os.Name));
            Assert.False(string.IsNullOrWhiteSpace(os.KernelVersion));
        }

        [Fact]
        public void ReadsTheWindowsVersion()
        {
            if (!OperatingSystem.IsWindows())
                return;

            var os = OSInfoSnapshotProvider.Get();
            var expected = Environment.OSVersion.Version;

            Assert.NotNull(os.Version);
            Assert.Equal((expected.Major, expected.Minor, expected.Build), (os.Version.Major, os.Version.Minor, os.Version.Build));
            Assert.StartsWith("Windows", os.Name);
            Assert.False(os.IsWsl);
        }

        [Fact]
        public void IsDetectedOnce() => Assert.Same(OSInfoSnapshotProvider.Get(), OSInfoSnapshotProvider.Get());

        [Theory]
        [InlineData("Windows 10 Pro", 26100, "Windows 11 Pro")]
        [InlineData("Windows 10 Pro", 19045, "Windows 10 Pro")]
        [InlineData("Windows Server 2025 Datacenter", 26100, "Windows Server 2025 Datacenter")]
        [InlineData(null, 26100, null)]
        public void CorrectsTheWindows11ProductName(string? productName, int build, string? expected) =>
            Assert.Equal(expected, OSDetector.WindowsProductName(productName, build));

        [Theory]
        [InlineData("24.04", "24.4")]
        [InlineData("12", "12.0")]
        [InlineData("3.20.3", "3.20.3")]
        [InlineData("14.1-RELEASE-p3", "14.1")]
        [InlineData("1.2.3.4.5", "1.2.3.4")]
        [InlineData("rolling", null)]
        [InlineData("", null)]
        [InlineData(null, null)]
        public void ParsesVersions(string? text, string? expected) =>
            Assert.Equal(expected is null ? null : Version.Parse(expected), OSDetector.ParseVersion(text));

        [Theory]
        [InlineData("12:memory:/docker/3f2a9c\n11:cpu:/docker/3f2a9c", true)]
        [InlineData("0::/kubepods/besteffort/pod1/abc", true)]
        [InlineData("0::/user.slice/user-1000.slice/session-2.scope", false)]
        [InlineData("0::/", false)]
        [InlineData(null, false)]
        public void RecognizesContainerCgroups(string? cgroup, bool expected) => Assert.Equal(expected, LinuxOS.IsContainerCgroup(cgroup));

        [Theory]
        [InlineData("5.15.153.1-microsoft-standard-WSL2", true)]
        [InlineData("4.4.0-19041-Microsoft", true)]
        [InlineData("6.8.0-45-generic", false)]
        [InlineData(null, false)]
        public void RecognizesWslKernels(string? kernel, bool expected) => Assert.Equal(expected, LinuxOS.IsWslKernel(kernel));
    }
}

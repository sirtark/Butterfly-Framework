using Butterfly.SystemInfo.OS;
using static Butterfly.Virtualization.VirtualizationHost;

namespace Butterfly.Virtualization.Core.Tests
{
    public class VirtualizationHostTests
    {
        private const long GiB = 1024L * 1024 * 1024;

        private static HostFacts Capable(OSFamily family = OSFamily.Windows, string name = "Windows 11 Pro") =>
            new(family, name, HardwareVirtualization: true, FirmwareVirtualization: true, SecondLevelAddressTranslation: true, IsVirtualMachine: false, TotalMemoryBytes: 16 * GiB);

        private static VirtualizationIssueKind[] Kinds(VirtualizationReadiness readiness) => [.. readiness.Issues.Select(issue => issue.Kind)];

        [Theory]
        [InlineData(OSFamily.Windows, HypervisorPlatform.HyperV)]
        [InlineData(OSFamily.Linux, HypervisorPlatform.KVM)]
        [InlineData(OSFamily.MacOS, HypervisorPlatform.AppleVirtualization)]
        [InlineData(OSFamily.FreeBSD, HypervisorPlatform.Bhyve)]
        public void ACapableHostIsReady(OSFamily family, HypervisorPlatform platform)
        {
            var readiness = Evaluate(Capable(family));

            Assert.True(readiness.IsReady);
            Assert.Empty(readiness.Issues);
            Assert.Equal(platform, readiness.Platform);
        }

        [Fact]
        public void AnUnknownOperatingSystemHasNoHypervisor()
        {
            var readiness = Evaluate(Capable(OSFamily.Unknown, "Plan 9"));

            Assert.False(readiness.IsReady);
            Assert.Equal([VirtualizationIssueKind.UnsupportedOperatingSystem], Kinds(readiness));
        }

        [Fact]
        public void WindowsHomeHasNoHyperV() =>
            Assert.Equal([VirtualizationIssueKind.HypervisorNotIncludedInEdition], Kinds(Evaluate(Capable(name: "Windows 11 Home"))));

        [Fact]
        public void AProcessorWithoutVirtualizationIsBlocking()
        {
            var readiness = Evaluate(Capable() with { HardwareVirtualization = false, FirmwareVirtualization = false, SecondLevelAddressTranslation = false });

            Assert.False(readiness.IsReady);
            // The firmware and SLAT problems are consequences of the first one: they are not reported again.
            Assert.Equal([VirtualizationIssueKind.HardwareVirtualizationUnsupported], Kinds(readiness));
        }

        [Fact]
        public void AGuestWithoutNestedVirtualizationIsBlocking()
        {
            var readiness = Evaluate(Capable() with { HardwareVirtualization = false, IsVirtualMachine = true });

            Assert.True(readiness.IsNested);
            Assert.Equal([VirtualizationIssueKind.NestedVirtualizationUnavailable], Kinds(readiness));
        }

        [Fact]
        public void DisabledFirmwareIsBlocking() =>
            Assert.Equal([VirtualizationIssueKind.DisabledInFirmware], Kinds(Evaluate(Capable() with { FirmwareVirtualization = false })));

        [Fact]
        public void MissingSlatOnlyBlocksHyperV()
        {
            Assert.False(Evaluate(Capable() with { SecondLevelAddressTranslation = false }).IsReady);

            var linux = Evaluate(Capable(OSFamily.Linux, "Ubuntu") with { SecondLevelAddressTranslation = false });
            Assert.True(linux.IsReady);
            Assert.Equal([VirtualizationIssueKind.SecondLevelAddressTranslationMissing], Kinds(linux));
        }

        [Fact]
        public void UnknownCapabilitiesAndLowMemoryAreWarnings()
        {
            var readiness = Evaluate(Capable() with { HardwareVirtualization = null, FirmwareVirtualization = null, SecondLevelAddressTranslation = null, TotalMemoryBytes = 2 * GiB });

            Assert.True(readiness.IsReady);
            Assert.Equal([VirtualizationIssueKind.CapabilitiesUnverified, VirtualizationIssueKind.LowMemory], Kinds(readiness));
        }

        [Fact]
        public void ChecksTheMachineRunningTheTests()
        {
            var readiness = CheckReadiness();

            if (OperatingSystem.IsWindows())
                Assert.Equal(HypervisorPlatform.HyperV, readiness.Platform);
            else if (OperatingSystem.IsLinux())
                Assert.Equal(HypervisorPlatform.KVM, readiness.Platform);
            Assert.All(readiness.Issues, issue => Assert.False(string.IsNullOrWhiteSpace(issue.Message)));
        }
    }
}

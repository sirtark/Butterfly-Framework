namespace Butterfly.Virtualization.Core.Tests
{
    public class VirtualMachineSpecTests
    {
        private const long GiB = 1024L * 1024 * 1024;
        private static readonly string DiskPath = OperatingSystem.IsWindows() ? @"C:\VMs\disk.vhdx" : "/var/lib/vms/disk.qcow2";

        private static VirtualMachineSpec Valid() => new()
        {
            Name = "build-agent",
            ProcessorCount = 2,
            MemoryBytes = 2 * GiB,
            Disks = [new VirtualDiskSpec { Path = DiskPath, SizeBytes = 20 * GiB }]
        };

        [Fact]
        public void AcceptsAValidSpecification() => Assert.Empty(Valid().Validate(hostProcessors: 8, hostMemoryBytes: 16 * GiB));

        [Fact]
        public void TheDefaultsFitTheMachineRunningTheTests() => Assert.Empty(new VirtualMachineSpec { Name = "defaults", ProcessorCount = 1, MemoryBytes = 64L * 1024 * 1024 }.Validate());

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("a/b")]
        [InlineData("a:b")]
        [InlineData("a\tb")]
        [InlineData(" padded")]
        public void RejectsInvalidNames(string name) => Assert.Single((Valid() with { Name = name }).Validate(8, 16 * GiB));

        [Fact]
        public void RejectsNamesThatAreTooLong() => Assert.Single((Valid() with { Name = new string('x', 101) }).Validate(8, 16 * GiB));

        [Theory]
        [InlineData(0)]
        [InlineData(9)]
        public void ProcessorsMustFitTheHost(int processors) => Assert.Single((Valid() with { ProcessorCount = processors }).Validate(8, 16 * GiB));

        [Theory]
        [InlineData(16L * 1024 * 1024)]
        [InlineData(2L * 1024 * 1024 * 1024 + 1024 * 1024)]
        [InlineData(32L * 1024 * 1024 * 1024)]
        public void MemoryMustBeAPlausibleMultipleOf2MiB(long memory) => Assert.Single((Valid() with { MemoryBytes = memory }).Validate(8, 16 * GiB));

        [Fact]
        public void SkipsHostChecksWhenTheHostIsUnknown() => Assert.Empty((Valid() with { ProcessorCount = 64, MemoryBytes = 64 * GiB }).Validate(0, 0));

        [Fact]
        public void SecureBootNeedsUefi() =>
            Assert.Single((Valid() with { Firmware = VirtualMachineFirmware.Bios, SecureBoot = true }).Validate(8, 16 * GiB));

        [Fact]
        public void DisksNeedAnAbsolutePathAndASaneSize()
        {
            var spec = Valid() with
            {
                Disks =
                [
                    new VirtualDiskSpec { Path = "relative.vhdx" },
                    new VirtualDiskSpec { Path = DiskPath, SizeBytes = 1000 },
                    new VirtualDiskSpec { Path = DiskPath }
                ]
            };

            var errors = spec.Validate(8, 16 * GiB);

            Assert.Equal(2, errors.Count);
            Assert.Contains("Disk 1", errors[0]);
            Assert.Contains("Disk 2", errors[1]);
        }

        [Fact]
        public void TheInstallationMediaNeedsAnAbsolutePath() =>
            Assert.Single((Valid() with { InstallationMedia = "ubuntu.iso" }).Validate(8, 16 * GiB));

        [Fact]
        public void ABlankNetworkIsAMistake() => Assert.Single((Valid() with { Network = " " }).Validate(8, 16 * GiB));

        [Fact]
        public void EnsureValidReportsEveryProblem()
        {
            var exception = Assert.Throws<ArgumentException>(() => new VirtualMachineSpec { Name = "", ProcessorCount = 0 }.EnsureValid());

            Assert.Contains("name is required", exception.Message);
            Assert.Contains("at least one processor", exception.Message);
        }
    }
}

using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;

namespace Butterfly.SystemInfo.CPU.Tests
{
    // Checks against the machine running the tests.
    public class CPUInfoSnapshotProviderTests
    {
        [Fact]
        public void DetectsTheArchitectureOfTheMachine()
        {
            var cpu = CPUInfoSnapshotProvider.Get();

            var expected = RuntimeInformation.OSArchitecture switch
            {
                Architecture.X64 => (CPUArchitecture.X86, CPUInstructionSet.X86_64),
                Architecture.X86 => (CPUArchitecture.X86, CPUInstructionSet.X86),
                Architecture.Arm64 => (CPUArchitecture.ARM, CPUInstructionSet.ARM64),
                _ => (cpu.Architecture, cpu.InstructionSet)
            };
            Assert.Equal(expected, (cpu.Architecture, cpu.InstructionSet));
        }

        [Fact]
        public void CountsProcessors()
        {
            var cpu = CPUInfoSnapshotProvider.Get();

            Assert.True(cpu.LogicalProcessorCount >= 1);
            Assert.InRange(cpu.PhysicalCoreCount, 0, cpu.LogicalProcessorCount);
            Assert.InRange(cpu.PackageCount, 0, Math.Max(cpu.PhysicalCoreCount, 1));
            Assert.Equal(Environment.ProcessorCount, cpu.ProcessAvailableProcessorCount);
            Assert.True(cpu.ProcessAvailableProcessorCount <= cpu.LogicalProcessorCount);

            if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                Assert.True(cpu.PhysicalCoreCount >= 1);
        }

        [Fact]
        public void ReadsVendorModelAndFeaturesWithCpuid()
        {
            if (!X86Base.IsSupported)
                return;

            var cpu = CPUInfoSnapshotProvider.Get();

            Assert.False(string.IsNullOrWhiteSpace(cpu.Vendor));
            Assert.False(string.IsNullOrWhiteSpace(cpu.Model));
            if (RuntimeInformation.OSArchitecture == Architecture.X64)
                Assert.True(cpu.Features.HasFlag(CPUFeatures.SSE | CPUFeatures.SSE2), "Every x64 processor implements SSE2.");
            if (Avx2.IsSupported)
                Assert.True(cpu.Features.HasFlag(CPUFeatures.AVX2), "The runtime uses AVX2, so CPUID must report it.");
        }

        [Fact]
        public void VirtualizationInformationIsConsistent()
        {
            var virtualization = CPUInfoSnapshotProvider.Get().Virtualization;

            if (virtualization.HypervisorPresent == false)
            {
                Assert.Equal(HypervisorVendor.None, virtualization.HypervisorVendor);
                Assert.False(virtualization.IsVirtualMachine);
            }
            if (virtualization.IsVirtualMachine == true)
                Assert.True(virtualization.HypervisorPresent);
            if (X86Base.IsSupported)
                Assert.NotNull(virtualization.HypervisorPresent);
        }

        [Fact]
        public void IsDetectedOnce()
        {
            Assert.Same(CPUInfoSnapshotProvider.Get(), CPUInfoSnapshotProvider.Get());
            Assert.Same(CPUInfoSnapshotProvider.Instance, CPUInfoSnapshotProvider.Instance);
        }
    }
}

using Butterfly.SystemInfo.CPU.Detection;
using Butterfly.SystemInfo.Native;

namespace Butterfly.SystemInfo.CPU.Tests
{
    // Parsing and mapping, with fixed inputs captured from real machines.
    public class CPUDetectionTests
    {
        [Theory]
        [InlineData("Microsoft Hv", HypervisorVendor.HyperV)]
        [InlineData("KVMKVMKVM", HypervisorVendor.KVM)]
        [InlineData("VMwareVMware", HypervisorVendor.VMware)]
        [InlineData("VBoxVBoxVBox", HypervisorVendor.VirtualBox)]
        [InlineData("XenVMMXenVMM", HypervisorVendor.Xen)]
        [InlineData("TCGTCGTCGTCG", HypervisorVendor.QEMU)]
        [InlineData("lrpepyh  vr", HypervisorVendor.Parallels)]
        [InlineData("bhyve bhyve", HypervisorVendor.Bhyve)]
        [InlineData("", HypervisorVendor.Unknown)]
        [InlineData("SomethingNew", HypervisorVendor.Unknown)]
        public void MapsCpuidSignatures(string signature, HypervisorVendor expected) =>
            Assert.Equal(expected, Hypervisors.FromCpuidSignature(signature));

        [Theory]
        [InlineData("Microsoft Corporation", "Virtual Machine", HypervisorVendor.HyperV)]
        [InlineData("Microsoft Corporation", "Surface Laptop 5", HypervisorVendor.Unknown)]
        [InlineData("QEMU", "Standard PC (Q35 + ICH9, 2009)", HypervisorVendor.QEMU)]
        [InlineData("Red Hat", "KVM", HypervisorVendor.KVM)]
        [InlineData("VMware, Inc.", "VMware Virtual Platform", HypervisorVendor.VMware)]
        [InlineData("innotek GmbH", "VirtualBox", HypervisorVendor.VirtualBox)]
        [InlineData("Xen", "HVM domU", HypervisorVendor.Xen)]
        [InlineData("Amazon EC2", "t3.micro", HypervisorVendor.KVM)]
        [InlineData("Amazon EC2", "m5.metal", HypervisorVendor.Unknown)]
        [InlineData("Google", "Google Compute Engine", HypervisorVendor.KVM)]
        [InlineData("Dell Inc.", "OptiPlex 7090", HypervisorVendor.Unknown)]
        [InlineData(null, null, HypervisorVendor.Unknown)]
        public void MapsSystemVendors(string? manufacturer, string? product, HypervisorVendor expected) =>
            Assert.Equal(expected, Hypervisors.FromSystemVendor(manufacturer, product));

        [Theory]
        [InlineData("none", HypervisorVendor.None)]
        [InlineData("hv", HypervisorVendor.HyperV)]
        [InlineData("bhyve", HypervisorVendor.Bhyve)]
        [InlineData("generic", HypervisorVendor.Unknown)]
        public void MapsFreeBsdVmGuest(string guest, HypervisorVendor expected) =>
            Assert.Equal(expected, Hypervisors.FromFreeBsdVmGuest(guest));

        [Theory]
        [InlineData("0-3", new[] { 0, 1, 2, 3 })]
        [InlineData("0-1,4,6-7\n", new[] { 0, 1, 4, 6, 7 })]
        [InlineData("5", new[] { 5 })]
        [InlineData("", new int[0])]
        [InlineData(null, new int[0])]
        public void ParsesKernelCpuLists(string? text, int[] expected) => Assert.Equal(expected, CpuList.Parse(text));

        [Theory]
        [InlineData("0x41", "ARM")]
        [InlineData("0x61", "Apple")]
        [InlineData("0xc0", "Ampere")]
        [InlineData("0x00", null)]
        [InlineData("garbage", null)]
        public void NamesArmImplementers(string implementer, string? expected) => Assert.Equal(expected, ArmImplementers.Name(implementer));

        [Fact]
        public void ReadsX86CpuInfo()
        {
            const string cpuinfo = """
                processor	: 0
                vendor_id	: AuthenticAMD
                model name	: AMD EPYC 7763 64-Core Processor
                flags		: fpu vme sse sse2 svm npt avx2

                processor	: 1
                vendor_id	: AuthenticAMD
                model name	: AMD EPYC 7763 64-Core Processor
                """;
            var facts = new CPUFacts();

            LinuxCpu.FillFromCpuInfo(facts, KeyValueText.Parse(cpuinfo, ':'));

            Assert.Equal("AuthenticAMD", facts.Vendor);
            Assert.Equal("AMD EPYC 7763 64-Core Processor", facts.Model);
            Assert.True(facts.SecondLevelAddressTranslation);
        }

        [Fact]
        public void ReadsArmCpuInfo()
        {
            const string cpuinfo = """
                processor	: 0
                BogoMIPS	: 243.75
                Features	: fp asimd evtstrm aes pmull sha1 sha2 crc32 atomics sve
                CPU implementer	: 0x41
                CPU architecture: 8
                """;
            var facts = new CPUFacts();

            LinuxCpu.FillFromCpuInfo(facts, KeyValueText.Parse(cpuinfo, ':'));

            Assert.Equal("ARM", facts.Vendor);
            Assert.Null(facts.Model);
            Assert.True(facts.Features.HasFlag(CPUFeatures.SVE));
            Assert.Null(facts.SecondLevelAddressTranslation);
        }

        [Fact]
        public void DecodesCpuidFeatureBits()
        {
            // Leaf 1 ECX: SSE3 (0), AES (25), AVX (28). EDX: SSE (25), SSE2 (26). Leaf 7 EBX: AVX2 (5).
            var features = X86Cpuid.Features(ecx1: 1 | 1 << 25 | 1 << 28, edx1: 1 << 25 | 1 << 26, ebx7: 1 << 5);

            Assert.Equal(CPUFeatures.SSE3 | CPUFeatures.AES | CPUFeatures.AVX | CPUFeatures.SSE | CPUFeatures.SSE2 | CPUFeatures.AVX2, features);
        }

        [Fact]
        public void DecodesCpuidStrings()
        {
            // "GenuineIntel" as returned in EBX, EDX, ECX.
            Assert.Equal("GenuineIntel", X86Cpuid.Ascii(0x756E6547, 0x49656E69, 0x6C65746E));
            Assert.Equal("KVMKVMKVM", X86Cpuid.Ascii(0x4B4D564B, 0x564B4D56, 0x0000004D));
        }

        [Fact]
        public void TheFirmwareRevealsTheHypervisorWhenCpuidCannot()
        {
            var facts = new CPUFacts { SystemManufacturer = "QEMU", SystemProduct = "KVM Virtual Machine" };

            CPUDetector.ResolveHypervisor(facts);

            Assert.True(facts.HypervisorPresent);
            Assert.Equal(HypervisorVendor.KVM, facts.HypervisorVendor);
            Assert.True(facts.IsVirtualMachine);
        }

        [Fact]
        public void BareMetalHasNoHypervisor()
        {
            var facts = new CPUFacts { HypervisorPresent = false, SystemManufacturer = "Dell Inc." };

            CPUDetector.ResolveHypervisor(facts);

            Assert.Equal(HypervisorVendor.None, facts.HypervisorVendor);
            Assert.False(facts.IsVirtualMachine);
        }

        [Fact]
        public void TheHyperVRootPartitionIsNotAVirtualMachine()
        {
            var facts = new CPUFacts { HypervisorPresent = true, HypervisorVendor = HypervisorVendor.HyperV, IsVirtualMachine = false };

            CPUDetector.ResolveHypervisor(facts);

            Assert.False(facts.IsVirtualMachine);
        }
    }
}

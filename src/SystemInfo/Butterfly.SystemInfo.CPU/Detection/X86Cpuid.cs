using System.Buffers.Binary;
using System.Runtime.Intrinsics.X86;
using System.Text;

namespace Butterfly.SystemInfo.CPU.Detection
{
    // Reads the processor directly with the CPUID instruction (Intel SDM vol. 2A, AMD APM vol. 3).
    internal static class X86Cpuid
    {
        private const int HypervisorLeaf = 0x40000000;
        private const int HypervisorFeaturesLeaf = 0x40000003;
        // KVM/QEMU with Hyper-V enlightenments report "Microsoft Hv" first and their own signature here.
        private const int SecondaryHypervisorLeaf = 0x40000100;
        private const int ExtendedLeaf = unchecked((int)0x80000000);

        public static bool IsAvailable => X86Base.IsSupported;

        public static void Fill(CPUFacts facts)
        {
            var (maxLeaf, vendorEbx, vendorEcx, vendorEdx) = X86Base.CpuId(0, 0);
            facts.Vendor = Ascii(vendorEbx, vendorEdx, vendorEcx) is { Length: > 0 } vendor ? vendor : null;

            var (_, _, ecx1, edx1) = maxLeaf >= 1 ? X86Base.CpuId(1, 0) : default;
            var ebx7 = maxLeaf >= 7 ? X86Base.CpuId(7, 0).Ebx : 0;

            var maxExtendedLeaf = (uint)X86Base.CpuId(ExtendedLeaf, 0).Eax;
            var ecxExtended1 = maxExtendedLeaf >= 0x80000001 ? X86Base.CpuId(ExtendedLeaf + 1, 0).Ecx : 0;
            if (maxExtendedLeaf >= 0x80000004)
                facts.Model = Brand();

            facts.Features = Features(ecx1, edx1, ebx7);

            bool vmx = Bit(ecx1, 5), svm = Bit(ecxExtended1, 2);
            facts.HardwareVirtualization = vmx || svm;
            // AMD reports Nested Paging in CPUID; Intel's EPT is only visible through MSRs (kernel mode).
            if (svm && maxExtendedLeaf >= 0x8000000A)
                facts.SecondLevelAddressTranslation = Bit(X86Base.CpuId(ExtendedLeaf + 0xA, 0).Edx, 0);

            facts.HypervisorPresent = Bit(ecx1, 31);
            if (facts.HypervisorPresent == true)
                DetectHypervisor(facts);
        }

        private static void DetectHypervisor(CPUFacts facts)
        {
            var (maxHypervisorLeaf, ebx, ecx, edx) = X86Base.CpuId(HypervisorLeaf, 0);
            var signature = Ascii(ebx, ecx, edx);
            var vendor = Hypervisors.FromCpuidSignature(signature);

            if (vendor == HypervisorVendor.HyperV)
            {
                var (_, ebx2, ecx2, edx2) = X86Base.CpuId(SecondaryHypervisorLeaf, 0);
                var secondarySignature = Ascii(ebx2, ecx2, edx2);
                var secondary = Hypervisors.FromCpuidSignature(secondarySignature);

                if (secondary is not (HypervisorVendor.Unknown or HypervisorVendor.HyperV))
                {
                    (vendor, signature) = (secondary, secondarySignature);
                }
                else if ((uint)maxHypervisorLeaf >= HypervisorFeaturesLeaf && Bit(X86Base.CpuId(HypervisorFeaturesLeaf, 0).Ebx, 0))
                {
                    // "CreatePartitions" privilege: this is the root partition, i.e. the physical Windows host with
                    // Hyper-V/VBS enabled. The hypervisor hides VT-x from it, but it obviously has (and uses) it.
                    facts.IsVirtualMachine = false;
                    facts.HardwareVirtualization = true;
                    facts.FirmwareVirtualization = true;
                    facts.SecondLevelAddressTranslation = true;
                }
            }

            facts.HypervisorVendor = vendor;
            facts.HypervisorSignature = signature.Length > 0 ? signature : null;
        }

        internal static CPUFeatures Features(int ecx1, int edx1, int ebx7)
        {
            var features = CPUFeatures.None;
            void Set(bool present, CPUFeatures feature) { if (present) features |= feature; }

            Set(Bit(edx1, 25), CPUFeatures.SSE);
            Set(Bit(edx1, 26), CPUFeatures.SSE2);
            Set(Bit(ecx1, 0), CPUFeatures.SSE3);
            Set(Bit(ecx1, 1), CPUFeatures.PCLMULQDQ);
            Set(Bit(ecx1, 9), CPUFeatures.SSSE3);
            Set(Bit(ecx1, 12), CPUFeatures.FMA);
            Set(Bit(ecx1, 19), CPUFeatures.SSE4_1);
            Set(Bit(ecx1, 20), CPUFeatures.SSE4_2 | CPUFeatures.CRC32);
            Set(Bit(ecx1, 23), CPUFeatures.POPCNT);
            Set(Bit(ecx1, 25), CPUFeatures.AES);
            Set(Bit(ecx1, 28), CPUFeatures.AVX);
            Set(Bit(ecx1, 30), CPUFeatures.RDRAND);

            Set(Bit(ebx7, 3), CPUFeatures.BMI1);
            Set(Bit(ebx7, 5), CPUFeatures.AVX2);
            Set(Bit(ebx7, 8), CPUFeatures.BMI2);
            Set(Bit(ebx7, 16), CPUFeatures.AVX512);
            Set(Bit(ebx7, 18), CPUFeatures.RDSEED);
            Set(Bit(ebx7, 29), CPUFeatures.SHA);

            return features;
        }

        private static string? Brand()
        {
            var registers = new int[12];
            for (var i = 0; i < 3; i++)
            {
                var (eax, ebx, ecx, edx) = X86Base.CpuId(ExtendedLeaf + 2 + i, 0);
                (registers[i * 4], registers[i * 4 + 1], registers[i * 4 + 2], registers[i * 4 + 3]) = (eax, ebx, ecx, edx);
            }

            // Brand strings are padded with spaces ("       Intel(R) Core(TM)...").
            var brand = string.Join(' ', Ascii(registers).Split(' ', StringSplitOptions.RemoveEmptyEntries));
            return brand.Length > 0 ? brand : null;
        }

        internal static string Ascii(params ReadOnlySpan<int> registers)
        {
            Span<byte> bytes = stackalloc byte[registers.Length * 4];
            for (var i = 0; i < registers.Length; i++)
                BinaryPrimitives.WriteInt32LittleEndian(bytes[(i * 4)..], registers[i]);

            var end = bytes.IndexOf((byte)0);
            return Encoding.ASCII.GetString(end >= 0 ? bytes[..end] : bytes).Trim();
        }

        private static bool Bit(int register, int bit) => (register & (1 << bit)) != 0;
    }
}

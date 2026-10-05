using System.Runtime.InteropServices;

namespace Butterfly.SystemInfo.CPU.Detection
{
    // What the detectors find. Each source only fills what is still unknown, from the most to the least reliable:
    // CPUID first (x86), then the operating system, then the firmware's system vendor (DMI / BIOS).
    internal sealed class CPUFacts
    {
        public string? Vendor;
        public string? Model;
        public CPUFeatures Features;
        public int LogicalProcessors;
        public int PhysicalCores;
        public int Packages;

        public bool? HardwareVirtualization;
        public bool? FirmwareVirtualization;
        public bool? SecondLevelAddressTranslation;
        public bool? HypervisorPresent;
        public HypervisorVendor HypervisorVendor = HypervisorVendor.Unknown;
        public string? HypervisorSignature;
        public bool? IsVirtualMachine;

        // Firmware identification (DMI on Linux, BIOS registry key on Windows): a virtual machine reports its hypervisor here.
        public string? SystemManufacturer;
        public string? SystemProduct;
    }

    internal static class CPUDetector
    {
        public static CPUInfoSnapshot Detect()
        {
            var (architecture, instructionSet) = Map(RuntimeInformation.OSArchitecture);
            var facts = new CPUFacts();

            // CPUID describes the real processor only when the process runs natively (not emulated x64 on ARM64).
            if (architecture == CPUArchitecture.X86 && X86Cpuid.IsAvailable)
                X86Cpuid.Fill(facts);
            else if (architecture == CPUArchitecture.ARM)
                ArmFeatures.Fill(facts);

            if (OperatingSystem.IsWindows())
                WindowsCpu.Fill(facts);
            else if (OperatingSystem.IsLinux())
                LinuxCpu.Fill(facts);
            else if (OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
                SysctlCpu.Fill(facts);

            ResolveHypervisor(facts);

            return new CPUInfoSnapshot
            {
                Architecture = architecture,
                InstructionSet = instructionSet,
                Features = facts.Features,
                Vendor = facts.Vendor,
                Model = facts.Model,
                LogicalProcessorCount = facts.LogicalProcessors > 0 ? facts.LogicalProcessors : Environment.ProcessorCount,
                PhysicalCoreCount = facts.PhysicalCores,
                PackageCount = facts.Packages,
                ProcessAvailableProcessorCount = Environment.ProcessorCount,
                Virtualization = new CPUVirtualizationInfo
                {
                    HardwareVirtualizationSupported = facts.HardwareVirtualization,
                    FirmwareVirtualizationEnabled = facts.FirmwareVirtualization,
                    SecondLevelAddressTranslation = facts.SecondLevelAddressTranslation,
                    HypervisorPresent = facts.HypervisorPresent,
                    HypervisorVendor = facts.HypervisorVendor,
                    HypervisorSignature = facts.HypervisorSignature,
                    IsVirtualMachine = facts.IsVirtualMachine
                }
            };
        }

        internal static void ResolveHypervisor(CPUFacts facts)
        {
            if (facts.HypervisorPresent != false && facts.HypervisorVendor == HypervisorVendor.Unknown)
            {
                var vendor = Hypervisors.FromSystemVendor(facts.SystemManufacturer, facts.SystemProduct);
                if (vendor != HypervisorVendor.Unknown)
                {
                    facts.HypervisorPresent = true;
                    facts.HypervisorVendor = vendor;
                }
            }

            if (facts.HypervisorPresent == false)
            {
                facts.HypervisorVendor = HypervisorVendor.None;
                facts.IsVirtualMachine = false;
            }
            else if (facts.HypervisorPresent == true)
            {
                facts.IsVirtualMachine ??= true;
            }
        }

        internal static (CPUArchitecture Architecture, CPUInstructionSet InstructionSet) Map(Architecture architecture) => architecture switch
        {
            Architecture.X86 => (CPUArchitecture.X86, CPUInstructionSet.X86),
            Architecture.X64 => (CPUArchitecture.X86, CPUInstructionSet.X86_64),
            Architecture.Armv6 => (CPUArchitecture.ARM, CPUInstructionSet.ARMv6),
            Architecture.Arm => (CPUArchitecture.ARM, CPUInstructionSet.ARMv7),
            Architecture.Arm64 => (CPUArchitecture.ARM, CPUInstructionSet.ARM64),
            Architecture.RiscV64 => (CPUArchitecture.RiscV, CPUInstructionSet.RV64),
            Architecture.Ppc64le => (CPUArchitecture.PowerPC, CPUInstructionSet.PowerPC64),
            Architecture.LoongArch64 => (CPUArchitecture.LoongArch, CPUInstructionSet.LoongArch64),
            Architecture.S390x => (CPUArchitecture.S390, CPUInstructionSet.S390X),
            _ => (CPUArchitecture.Unknown, CPUInstructionSet.Unknown)
        };
    }
}

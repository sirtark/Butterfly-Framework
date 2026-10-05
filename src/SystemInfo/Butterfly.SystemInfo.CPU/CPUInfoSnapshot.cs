namespace Butterfly.SystemInfo.CPU
{
    public sealed record CPUInfoSnapshot
    {
        internal CPUInfoSnapshot()
        { }

        public CPUArchitecture Architecture { get; internal init; } = CPUArchitecture.Unknown;
        public CPUInstructionSet InstructionSet { get; internal init; } = CPUInstructionSet.Unknown;
        public CPUFeatures Features { get; internal init; } = CPUFeatures.None;
        public string? Vendor { get; internal init; }
        public string? Model { get; internal init; }

        /// <summary>Logical processors (hardware threads) of the machine. 0 when unknown.</summary>
        public int LogicalProcessorCount { get; internal init; }
        /// <summary>Physical cores of the machine. 0 when unknown.</summary>
        public int PhysicalCoreCount { get; internal init; }
        /// <summary>Physical processor packages (sockets). 0 when unknown.</summary>
        public int PackageCount { get; internal init; }
        /// <summary>Processors this process may use: less than <see cref="LogicalProcessorCount"/> under an affinity mask or a container CPU quota.</summary>
        public int ProcessAvailableProcessorCount { get; internal init; }

        public CPUVirtualizationInfo Virtualization { get; internal init; } = CPUVirtualizationInfo.Unknown;

        public static CPUInfoSnapshot Unknown => new();
    }

    /// <summary>
    /// Hardware virtualization capabilities and the hypervisor the machine runs on. A null value means it could not be
    /// determined (e.g. a guest without nested virtualization, or a platform that does not expose it to user mode).
    /// </summary>
    public sealed record CPUVirtualizationInfo
    {
        internal CPUVirtualizationInfo()
        { }

        /// <summary>The processor implements hardware virtualization (Intel VT-x, AMD-V, Apple/ARM EL2).</summary>
        public bool? HardwareVirtualizationSupported { get; internal init; }
        /// <summary>Hardware virtualization is enabled in the firmware (BIOS/UEFI) and available to the operating system.</summary>
        public bool? FirmwareVirtualizationEnabled { get; internal init; }
        /// <summary>Second Level Address Translation (Intel EPT, AMD NPT): required by Hyper-V and most modern hypervisors.</summary>
        public bool? SecondLevelAddressTranslation { get; internal init; }

        /// <summary>
        /// A hypervisor runs underneath the operating system. It is also true on a Windows host with Hyper-V (or VBS) enabled,
        /// where Windows itself runs on the hypervisor as the root partition; see <see cref="IsVirtualMachine"/>.
        /// </summary>
        public bool? HypervisorPresent { get; internal init; }
        public HypervisorVendor HypervisorVendor { get; internal init; } = HypervisorVendor.Unknown;
        /// <summary>Raw CPUID hypervisor signature (e.g. "Microsoft Hv", "KVMKVMKVM"), when the processor reports one.</summary>
        public string? HypervisorSignature { get; internal init; }
        /// <summary>The operating system is a guest of a hypervisor. False for the Hyper-V root partition (the physical host).</summary>
        public bool? IsVirtualMachine { get; internal init; }

        public static CPUVirtualizationInfo Unknown => new();
    }
}

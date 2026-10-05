namespace Butterfly.SystemInfo.CPU
{
    public enum CPUArchitecture : ushort
    {
        Unknown,

        X86,
        ARM,
        RiscV,
        PowerPC,
        MIPS,
        SPARC,
        Alpha,
        SuperH,
        ZISC,
        LoongArch,
        S390
    }
    public enum CPUInstructionSet : ushort
    {
        Unknown,

        X86,
        X86_64,

        ARMv7,
        ARM64,

        RV32,
        RV64,

        PowerPC32,
        PowerPC64,

        MIPS32,
        MIPS64,

        ARMv6,
        LoongArch64,
        S390X
    }

    /// <summary>Instruction set extensions implemented by the processor (not whether the runtime uses them).</summary>
    [Flags]
    public enum CPUFeatures : ulong
    {
        None = 0,

        // x86
        SSE = 1UL << 0,
        SSE2 = 1UL << 1,
        SSE3 = 1UL << 2,
        SSSE3 = 1UL << 3,
        SSE4_1 = 1UL << 4,
        SSE4_2 = 1UL << 5,

        AVX = 1UL << 6,
        AVX2 = 1UL << 7,
        AVX512 = 1UL << 8,

        AES = 1UL << 9,

        // ARM
        NEON = 1UL << 10,
        SVE = 1UL << 11,

        // Otros
        RDRAND = 1UL << 12,
        RDSEED = 1UL << 13,

        // x86
        POPCNT = 1UL << 14,
        FMA = 1UL << 15,
        BMI1 = 1UL << 16,
        BMI2 = 1UL << 17,
        PCLMULQDQ = 1UL << 18,

        // x86 (SHA-NI) and ARM (SHA2)
        SHA = 1UL << 19,
        // ARM (CRC32), x86 exposes it as part of SSE4.2
        CRC32 = 1UL << 20
    }

    /// <summary>Hypervisor that runs underneath the operating system.</summary>
    public enum HypervisorVendor : byte
    {
        /// <summary>It could not be determined whether there is a hypervisor, or which one it is.</summary>
        Unknown,
        /// <summary>The operating system runs directly on the hardware.</summary>
        None,

        HyperV,
        KVM,
        VMware,
        VirtualBox,
        Xen,
        /// <summary>QEMU without hardware acceleration (TCG). QEMU with KVM reports <see cref="KVM"/>.</summary>
        QEMU,
        Parallels,
        Bhyve,
        /// <summary>Apple's Hypervisor/Virtualization framework (Apple Silicon guests).</summary>
        AppleHypervisor
    }
}

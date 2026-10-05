using System.Runtime.Intrinsics.Arm;

namespace Butterfly.SystemInfo.CPU.Detection
{
    // ARM has no user-mode CPUID: the runtime already asked the OS (HWCAP, IsProcessorFeaturePresent, sysctl).
    // SVE is filled later from the OS, because the runtime only reports it when it can use it.
    internal static class ArmFeatures
    {
        public static void Fill(CPUFacts facts)
        {
            if (AdvSimd.IsSupported)
                facts.Features |= CPUFeatures.NEON;
            if (Aes.IsSupported)
                facts.Features |= CPUFeatures.AES;
            if (Crc32.IsSupported)
                facts.Features |= CPUFeatures.CRC32;
            if (Sha256.IsSupported)
                facts.Features |= CPUFeatures.SHA;
        }
    }
}

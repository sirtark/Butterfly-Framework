using Butterfly.SystemInfo.Native;
using System.Numerics;
using System.Runtime.Versioning;

namespace Butterfly.SystemInfo.CPU.Detection
{
    [SupportedOSPlatform("windows")]
    internal static unsafe class WindowsCpu
    {
        private const string ProcessorKey = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";
        private const string BiosKey = @"HARDWARE\DESCRIPTION\System\BIOS";

        public static void Fill(CPUFacts facts)
        {
            facts.Vendor ??= WindowsRegistry.GetString(ProcessorKey, "VendorIdentifier");
            facts.Model ??= WindowsRegistry.GetString(ProcessorKey, "ProcessorNameString");

            facts.PhysicalCores = Count(Kernel32.RelationProcessorCore, out var logicalProcessors);
            facts.LogicalProcessors = logicalProcessors;
            facts.Packages = Count(Kernel32.RelationProcessorPackage, out _);

            facts.FirmwareVirtualization ??= Kernel32.IsProcessorFeaturePresent(Kernel32.PFVirtFirmwareEnabled);
            facts.SecondLevelAddressTranslation ??= Kernel32.IsProcessorFeaturePresent(Kernel32.PFSecondLevelAddressTranslation);
            if (Kernel32.IsProcessorFeaturePresent(Kernel32.PFArmSveInstructionsAvailable))
                facts.Features |= CPUFeatures.SVE;

            facts.SystemManufacturer = WindowsRegistry.GetString(BiosKey, "SystemManufacturer");
            facts.SystemProduct = WindowsRegistry.GetString(BiosKey, "SystemProductName");
        }

        // Number of SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX entries of a relationship, and the logical processors they cover.
        private static int Count(int relationship, out int logicalProcessors)
        {
            logicalProcessors = 0;

            uint length = 0;
            Kernel32.GetLogicalProcessorInformationEx(relationship, null, &length);
            if (length == 0)
                return 0;

            var buffer = new byte[length];
            fixed (byte* start = buffer)
            {
                if (!Kernel32.GetLogicalProcessorInformationEx(relationship, start, &length))
                    return 0;

                // Entry: Relationship (4), Size (4), PROCESSOR_RELATIONSHIP { Flags, EfficiencyClass, Reserved[20],
                // GroupCount (2), GROUP_AFFINITY GroupMask[] } where GROUP_AFFINITY is { KAFFINITY Mask, Group, Reserved[3] }.
                var affinitySize = IntPtr.Size + 8;
                var count = 0;
                for (uint offset = 0; offset + 32 <= length;)
                {
                    var entry = start + offset;
                    var size = *(uint*)(entry + 4);
                    if (size == 0)
                        break;

                    var groupCount = *(ushort*)(entry + 30);
                    for (var group = 0; group < groupCount; group++)
                        logicalProcessors += BitOperations.PopCount(*(nuint*)(entry + 32 + group * affinitySize));

                    count++;
                    offset += size;
                }
                return count;
            }
        }
    }
}

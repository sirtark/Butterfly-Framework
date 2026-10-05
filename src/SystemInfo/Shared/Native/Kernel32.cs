using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Butterfly.SystemInfo.Native
{
    [SupportedOSPlatform("windows")]
    internal static unsafe partial class Kernel32
    {
        private const string Library = "kernel32.dll";

        // IsProcessorFeaturePresent
        public const uint PFSecondLevelAddressTranslation = 20;
        public const uint PFVirtFirmwareEnabled = 21;
        public const uint PFArmSveInstructionsAvailable = 46;

        // LOGICAL_PROCESSOR_RELATIONSHIP
        public const int RelationProcessorCore = 0;
        public const int RelationProcessorPackage = 3;

        [StructLayout(LayoutKind.Sequential)]
        public struct MemoryStatusEx
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhys;
            public ulong AvailPhys;
            public ulong TotalPageFile;
            public ulong AvailPageFile;
            public ulong TotalVirtual;
            public ulong AvailVirtual;
            public ulong AvailExtendedVirtual;
        }

        [LibraryImport(Library, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool GlobalMemoryStatusEx(MemoryStatusEx* buffer);

        [LibraryImport(Library)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool IsProcessorFeaturePresent(uint processorFeature);

        [LibraryImport(Library, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool GetLogicalProcessorInformationEx(int relationshipType, byte* buffer, uint* returnedLength);
    }
}

using Butterfly.SystemInfo.Memory.Detection;

namespace Butterfly.SystemInfo.Memory
{
    public sealed class MemoryInfoSnapshotProvider : ISystemInfoSnapshotProvider<MemoryInfoSnapshotProvider, MemoryInfoSnapshot>
    {
        private MemoryInfoSnapshotProvider()
        { }

        // Memory usage changes all the time: every call reads it again.
        public static MemoryInfoSnapshot Get() => MemoryDetector.Detect();

        public static MemoryInfoSnapshot Unknown => MemoryInfoSnapshot.Unknown;

        public static object Instance => (field ??= new());
    }
}

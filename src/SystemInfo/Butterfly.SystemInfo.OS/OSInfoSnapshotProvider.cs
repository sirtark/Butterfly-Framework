using Butterfly.SystemInfo.OS.Detection;

namespace Butterfly.SystemInfo.OS
{
    public sealed class OSInfoSnapshotProvider : ISystemInfoSnapshotProvider<OSInfoSnapshotProvider, OSInfoSnapshot>
    {
        // The operating system does not change while the process runs: it is detected once.
        private static readonly Lazy<OSInfoSnapshot> Snapshot = new(OSDetector.Detect);

        private OSInfoSnapshotProvider()
        { }
        public static OSInfoSnapshot Get() => Snapshot.Value;

        public static OSInfoSnapshot Unknown => OSInfoSnapshot.Unknown;

        public static object Instance => (field ??= new());
    }
}

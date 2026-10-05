using Butterfly.SystemInfo.CPU.Detection;

namespace Butterfly.SystemInfo.CPU
{
    public sealed class CPUInfoSnapshotProvider : ISystemInfoSnapshotProvider<CPUInfoSnapshotProvider, CPUInfoSnapshot>
    {
        // The processor does not change while the process runs: it is detected once.
        private static readonly Lazy<CPUInfoSnapshot> Snapshot = new(CPUDetector.Detect);

        private CPUInfoSnapshotProvider()
        { }
        public static CPUInfoSnapshot Get() => Snapshot.Value;

        public static CPUInfoSnapshot Unknown => CPUInfoSnapshot.Unknown;

        public static object Instance => (field ??= new());
    }
}

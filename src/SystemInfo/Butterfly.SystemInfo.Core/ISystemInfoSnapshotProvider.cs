using Butterfly.DesignPatterns.Creational;

namespace Butterfly.SystemInfo
{
    public interface ISystemInfoSnapshotProvider<TSelf, TInfoSnapshot> : ISingleton<TSelf> where TInfoSnapshot : class where TSelf : ISystemInfoSnapshotProvider<TSelf, TInfoSnapshot>
    {
        /// <summary>Detects the information of the machine. It never throws: whatever cannot be detected stays unknown.</summary>
        public abstract static TInfoSnapshot Get();
        public abstract static TInfoSnapshot Unknown { get; }
    }
}

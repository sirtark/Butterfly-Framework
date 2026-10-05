namespace Butterfly.Virtualization
{
    public enum VirtualMachineState : byte
    {
        Unknown,
        Off,
        Starting,
        Running,
        Stopping,
        Paused,
        Saving,
        /// <summary>Stopped with its memory saved to disk; starting it resumes where it was.</summary>
        Saved,
        Crashed
    }

    public sealed record VirtualMachine
    {
        /// <summary>Identifier assigned by the hypervisor (Hyper-V VMId, libvirt UUID).</summary>
        public required string Id { get; init; }
        public required string Name { get; init; }
        public VirtualMachineState State { get; init; }
        public int ProcessorCount { get; init; }
        /// <summary>Memory configured for the machine.</summary>
        public long MemoryBytes { get; init; }
        /// <summary>Time since the machine started, when the hypervisor reports it.</summary>
        public TimeSpan? Uptime { get; init; }
    }

    /// <summary>A saved point in time of a machine (Hyper-V checkpoint, libvirt snapshot) that it can be restored to.</summary>
    public sealed record VirtualMachineCheckpoint
    {
        public required string Name { get; init; }
        public DateTimeOffset? CreatedAt { get; init; }
    }
}

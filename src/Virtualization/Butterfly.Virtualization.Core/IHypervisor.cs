namespace Butterfly.Virtualization
{
    public enum HypervisorPlatform : byte
    {
        Unknown,
        /// <summary>Microsoft Hyper-V (Windows).</summary>
        HyperV,
        /// <summary>KVM/QEMU, usually managed through libvirt (Linux).</summary>
        KVM,
        /// <summary>Apple Virtualization framework (macOS).</summary>
        AppleVirtualization,
        /// <summary>bhyve (FreeBSD).</summary>
        Bhyve
    }

    /// <summary>
    /// Manages the virtual machines of one hypervisor. Machines are identified by name. Every operation throws
    /// <see cref="HypervisorUnavailableException"/> when the hypervisor is not installed or not running, and
    /// <see cref="VirtualMachineNotFoundException"/> when the machine does not exist.
    /// </summary>
    public interface IHypervisor
    {
        public HypervisorPlatform Platform { get; }

        /// <summary>The hypervisor is installed, running and this process is allowed to manage it.</summary>
        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

        public Task<IReadOnlyList<VirtualMachine>> ListAsync(CancellationToken cancellationToken = default);
        /// <returns>The machine, or null when it does not exist.</returns>
        public Task<VirtualMachine?> FindAsync(string name, CancellationToken cancellationToken = default);

        /// <summary>Defines a new machine, creating the disks that do not exist yet. It is left powered off.</summary>
        /// <exception cref="ArgumentException">The specification is not valid for this host (see <see cref="VirtualMachineSpec.Validate()"/>).</exception>
        /// <exception cref="VirtualMachineAlreadyExistsException">A machine with that name already exists.</exception>
        public Task<VirtualMachine> CreateAsync(VirtualMachineSpec spec, CancellationToken cancellationToken = default);
        /// <summary>Removes the machine (turning it off first). Its disks are kept.</summary>
        public Task DeleteAsync(string name, CancellationToken cancellationToken = default);

        public Task StartAsync(string name, CancellationToken cancellationToken = default);
        /// <summary>Asks the guest operating system to shut down. It returns before the machine is off; see <see cref="HypervisorExtensions.WaitForStateAsync"/>.</summary>
        public Task ShutdownAsync(string name, CancellationToken cancellationToken = default);
        /// <summary>Powers the machine off immediately, like pulling the plug.</summary>
        public Task TurnOffAsync(string name, CancellationToken cancellationToken = default);
        /// <summary>Freezes the machine in memory.</summary>
        public Task PauseAsync(string name, CancellationToken cancellationToken = default);
        public Task ResumeAsync(string name, CancellationToken cancellationToken = default);
        /// <summary>Saves the memory of the machine to disk and stops it; <see cref="StartAsync"/> restores it.</summary>
        public Task SaveAsync(string name, CancellationToken cancellationToken = default);

        public Task<IReadOnlyList<VirtualMachineCheckpoint>> ListCheckpointsAsync(string name, CancellationToken cancellationToken = default);
        public Task<VirtualMachineCheckpoint> CreateCheckpointAsync(string name, string checkpointName, CancellationToken cancellationToken = default);
        /// <exception cref="CheckpointNotFoundException">The machine has no checkpoint with that name.</exception>
        public Task RestoreCheckpointAsync(string name, string checkpointName, CancellationToken cancellationToken = default);
        /// <exception cref="CheckpointNotFoundException">The machine has no checkpoint with that name.</exception>
        public Task DeleteCheckpointAsync(string name, string checkpointName, CancellationToken cancellationToken = default);
    }
}

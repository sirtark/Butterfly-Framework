namespace Butterfly.Virtualization
{
    public class VirtualizationException : Exception
    {
        public VirtualizationException(string message) : base(message)
        { }
        public VirtualizationException(string message, Exception? innerException) : base(message, innerException)
        { }
    }

    /// <summary>The hypervisor is not installed, not running, or this process is not allowed to manage it.</summary>
    public sealed class HypervisorUnavailableException(HypervisorPlatform platform, string message)
        : VirtualizationException(message)
    {
        public HypervisorPlatform Platform { get; } = platform;
    }

    public sealed class VirtualMachineNotFoundException(string name)
        : VirtualizationException($"The virtual machine '{name}' does not exist.")
    {
        public string Name { get; } = name;
    }

    public sealed class VirtualMachineAlreadyExistsException(string name)
        : VirtualizationException($"A virtual machine named '{name}' already exists.")
    {
        public string Name { get; } = name;
    }

    public sealed class CheckpointNotFoundException(string virtualMachine, string checkpoint)
        : VirtualizationException($"The virtual machine '{virtualMachine}' has no checkpoint named '{checkpoint}'.")
    {
        public string VirtualMachine { get; } = virtualMachine;
        public string Checkpoint { get; } = checkpoint;
    }

    /// <summary>The operation is not possible in the current state of the machine (e.g. pausing a machine that is off).</summary>
    public sealed class InvalidVirtualMachineStateException(string name, string message)
        : VirtualizationException(message)
    {
        public string Name { get; } = name;
    }
}

using Butterfly.SystemInfo.CPU;
using Butterfly.SystemInfo.Memory;

namespace Butterfly.Virtualization
{
    public enum VirtualMachineFirmware : byte
    {
        /// <summary>UEFI (Hyper-V generation 2, libvirt OVMF/AAVMF). Required for secure boot.</summary>
        Uefi,
        /// <summary>Legacy BIOS (Hyper-V generation 1).</summary>
        Bios
    }

    /// <summary>A virtual hard disk. When <see cref="SizeBytes"/> is set and the file does not exist, it is created (dynamically expanding).</summary>
    public sealed record VirtualDiskSpec
    {
        /// <summary>Absolute path of the disk image (.vhdx on Hyper-V; .qcow2 or .img/.raw on libvirt).</summary>
        public required string Path { get; init; }
        public long? SizeBytes { get; init; }
    }

    public sealed record VirtualMachineSpec
    {
        public const long MinimumMemoryBytes = 32L * 1024 * 1024;
        /// <summary>Hyper-V only accepts memory sizes that are multiples of 2 MiB.</summary>
        public const long MemoryGranularityBytes = 2L * 1024 * 1024;
        public const int MaximumNameLength = 100;

        private static readonly char[] InvalidNameCharacters = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

        public required string Name { get; init; }
        public int ProcessorCount { get; init; } = 2;
        public long MemoryBytes { get; init; } = 2L * 1024 * 1024 * 1024;
        public VirtualMachineFirmware Firmware { get; init; } = VirtualMachineFirmware.Uefi;
        public bool SecureBoot { get; init; }
        /// <summary>Disks in boot order.</summary>
        public IReadOnlyList<VirtualDiskSpec> Disks { get; init; } = [];
        /// <summary>Absolute path of an ISO image inserted as a DVD; the machine boots from it first.</summary>
        public string? InstallationMedia { get; init; }
        /// <summary>Network to connect to (Hyper-V virtual switch, libvirt network). Null leaves the machine disconnected.</summary>
        public string? Network { get; init; }

        /// <summary>Checks the specification against itself and the resources of this machine.</summary>
        /// <returns>The problems found; empty when it is valid.</returns>
        public IReadOnlyList<string> Validate()
        {
            var cpu = CPUInfoSnapshotProvider.Get();
            var memory = MemoryInfoSnapshotProvider.Get();
            return Validate(cpu.LogicalProcessorCount, memory.TotalPhysicalBytes);
        }

        /// <param name="hostProcessors">Logical processors of the host; 0 or less skips the check.</param>
        /// <param name="hostMemoryBytes">Physical memory of the host; 0 or less skips the check.</param>
        public IReadOnlyList<string> Validate(int hostProcessors, long hostMemoryBytes)
        {
            var errors = new List<string>();

            if (string.IsNullOrWhiteSpace(Name))
                errors.Add("The name is required.");
            else if (Name.Length > MaximumNameLength)
                errors.Add($"The name cannot be longer than {MaximumNameLength} characters.");
            else if (Name.IndexOfAny(InvalidNameCharacters) >= 0 || Name.Any(char.IsControl))
                errors.Add($"The name cannot contain control characters or any of {string.Join(' ', InvalidNameCharacters)}.");
            else if (Name.Trim().Length != Name.Length)
                errors.Add("The name cannot start or end with white space.");

            if (ProcessorCount < 1)
                errors.Add("The machine needs at least one processor.");
            else if (hostProcessors > 0 && ProcessorCount > hostProcessors)
                errors.Add($"The machine cannot have more processors ({ProcessorCount}) than the host ({hostProcessors}).");

            if (MemoryBytes < MinimumMemoryBytes)
                errors.Add($"The machine needs at least {MinimumMemoryBytes / (1024 * 1024)} MiB of memory.");
            else if (MemoryBytes % MemoryGranularityBytes != 0)
                errors.Add("The memory must be a multiple of 2 MiB.");
            else if (hostMemoryBytes > 0 && MemoryBytes > hostMemoryBytes)
                errors.Add($"The machine cannot have more memory ({MemoryBytes} bytes) than the host ({hostMemoryBytes} bytes).");

            if (SecureBoot && Firmware != VirtualMachineFirmware.Uefi)
                errors.Add("Secure boot requires UEFI firmware.");

            for (var i = 0; i < Disks.Count; i++)
            {
                var disk = Disks[i];
                if (disk is null || string.IsNullOrWhiteSpace(disk.Path) || !System.IO.Path.IsPathFullyQualified(disk.Path))
                    errors.Add($"Disk {i + 1} needs an absolute path.");
                else if (disk.SizeBytes is { } size && (size < 1024 * 1024 || size % 512 != 0))
                    errors.Add($"Disk {i + 1} must be at least 1 MiB and a multiple of 512 bytes.");
            }

            if (InstallationMedia is not null && !System.IO.Path.IsPathFullyQualified(InstallationMedia))
                errors.Add("The installation media needs an absolute path.");

            if (Network is not null && string.IsNullOrWhiteSpace(Network))
                errors.Add("The network name cannot be blank; use null to leave the machine disconnected.");

            return errors;
        }

        /// <exception cref="ArgumentException">The specification is not valid for this host.</exception>
        public void EnsureValid()
        {
            var errors = Validate();
            if (errors.Count > 0)
                throw new ArgumentException($"Invalid virtual machine specification: {string.Join(" ", errors)}", "spec");
        }
    }
}

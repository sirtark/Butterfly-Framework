using Butterfly.Virtualization.Native;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Butterfly.Virtualization.Libvirt
{
    /// <summary>
    /// Manages KVM/QEMU virtual machines (libvirt domains) through virsh. Needs the libvirt client tools (virsh) and,
    /// to create disks, qemu-img. The system connection requires root or membership of the "libvirt" group.
    /// Disks are created on this machine, so a remote connection URI must point to storage this machine shares.
    /// </summary>
    public sealed partial class LibvirtHypervisor : IHypervisor
    {
        /// <summary>Machines of the whole system (the default).</summary>
        public const string SystemConnection = "qemu:///system";
        /// <summary>Machines of the current user, without privileges (user-mode networking only).</summary>
        public const string SessionConnection = "qemu:///session";

        private const string Virsh = "virsh";
        private const string QemuImg = "qemu-img";

        // virsh messages are translated; the C locale keeps them stable for classification.
        private static readonly Dictionary<string, string> Environment = new() { ["LC_ALL"] = "C" };

        private readonly ICommandRunner runner;
        private readonly Func<bool> hasKvm;
        private readonly bool x86;

        public LibvirtHypervisor(string connectionUri = SystemConnection)
            : this(connectionUri, ProcessCommandRunner.Instance, () => File.Exists("/dev/kvm"), RuntimeInformation.OSArchitecture is Architecture.X64 or Architecture.X86)
        { }
        internal LibvirtHypervisor(string connectionUri, ICommandRunner runner, Func<bool> hasKvm, bool x86)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(connectionUri);
            ConnectionUri = connectionUri;
            this.runner = runner;
            this.hasKvm = hasKvm;
            this.x86 = x86;
        }

        public string ConnectionUri { get; }

        public HypervisorPlatform Platform => HypervisorPlatform.KVM;

        public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                await VirshAsync(["uri"], null, null, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (VirtualizationException)
            {
                return false;
            }
        }

        public async Task<IReadOnlyList<VirtualMachine>> ListAsync(CancellationToken cancellationToken = default)
        {
            var names = await VirshAsync(["list", "--all", "--name"], null, null, cancellationToken).ConfigureAwait(false);
            var machines = new List<VirtualMachine>();
            foreach (var name in Lines(names))
            {
                // A machine may disappear between both calls.
                if (await FindAsync(name, cancellationToken).ConfigureAwait(false) is { } machine)
                    machines.Add(machine);
            }
            return machines;
        }

        public async Task<VirtualMachine?> FindAsync(string name, CancellationToken cancellationToken = default)
        {
            try
            {
                return ParseDomainInfo(await VirshAsync(["dominfo", "--domain", name], name, null, cancellationToken).ConfigureAwait(false));
            }
            catch (VirtualMachineNotFoundException)
            {
                return null;
            }
        }

        public async Task<VirtualMachine> CreateAsync(VirtualMachineSpec spec, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(spec);
            spec.EnsureValid();
            if (!x86 && spec.Firmware == VirtualMachineFirmware.Bios)
                throw new ArgumentException("ARM virtual machines only boot with UEFI firmware.", nameof(spec));
            if (await FindAsync(spec.Name, cancellationToken).ConfigureAwait(false) is not null)
                throw new VirtualMachineAlreadyExistsException(spec.Name);

            foreach (var disk in spec.Disks)
            {
                if (disk.SizeBytes is { } size && !File.Exists(disk.Path))
                    await CreateDiskAsync(disk.Path, size, cancellationToken).ConfigureAwait(false);
            }

            var definition = Path.Combine(Path.GetTempPath(), $"butterfly-{Guid.NewGuid():N}.xml");
            try
            {
                LibvirtDomainXml.Build(spec, hasKvm(), x86).Save(definition);
                await VirshAsync(["define", "--file", definition], spec.Name, null, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                File.Delete(definition);
            }

            return await this.GetAsync(spec.Name, cancellationToken).ConfigureAwait(false);
        }

        public async Task DeleteAsync(string name, CancellationToken cancellationToken = default)
        {
            var machine = await this.GetAsync(name, cancellationToken).ConfigureAwait(false);
            if (machine.State is not (VirtualMachineState.Off or VirtualMachineState.Saved))
                await VirshAsync(["destroy", "--domain", name], name, null, cancellationToken).ConfigureAwait(false);

            await VirshAsync(["undefine", "--domain", name, "--managed-save", "--snapshots-metadata", "--nvram"], name, null, cancellationToken).ConfigureAwait(false);
        }

        public Task StartAsync(string name, CancellationToken cancellationToken = default) =>
            TransitionAsync(name, VirtualMachineState.Running, [VirtualMachineState.Off, VirtualMachineState.Saved, VirtualMachineState.Crashed], ["start", "--domain", name], cancellationToken);

        public Task ShutdownAsync(string name, CancellationToken cancellationToken = default) =>
            TransitionAsync(name, VirtualMachineState.Off, [VirtualMachineState.Running], ["shutdown", "--domain", name], cancellationToken);

        public async Task TurnOffAsync(string name, CancellationToken cancellationToken = default)
        {
            var machine = await this.GetAsync(name, cancellationToken).ConfigureAwait(false);
            if (machine.State == VirtualMachineState.Off)
                return;

            // A saved machine is already stopped: turning it off discards the saved memory, as Hyper-V does.
            string[] command = machine.State == VirtualMachineState.Saved ? ["managedsave-remove", "--domain", name] : ["destroy", "--domain", name];
            await VirshAsync(command, name, null, cancellationToken).ConfigureAwait(false);
        }

        public Task PauseAsync(string name, CancellationToken cancellationToken = default) =>
            TransitionAsync(name, VirtualMachineState.Paused, [VirtualMachineState.Running], ["suspend", "--domain", name], cancellationToken);

        public Task ResumeAsync(string name, CancellationToken cancellationToken = default) =>
            TransitionAsync(name, VirtualMachineState.Running, [VirtualMachineState.Paused], ["resume", "--domain", name], cancellationToken);

        public Task SaveAsync(string name, CancellationToken cancellationToken = default) =>
            TransitionAsync(name, VirtualMachineState.Saved, [VirtualMachineState.Running, VirtualMachineState.Paused], ["managedsave", "--domain", name], cancellationToken);

        public async Task<IReadOnlyList<VirtualMachineCheckpoint>> ListCheckpointsAsync(string name, CancellationToken cancellationToken = default) =>
            ParseSnapshotList(await VirshAsync(["snapshot-list", "--domain", name], name, null, cancellationToken).ConfigureAwait(false));

        public async Task<VirtualMachineCheckpoint> CreateCheckpointAsync(string name, string checkpointName, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(checkpointName);
            if ((await ListCheckpointsAsync(name, cancellationToken).ConfigureAwait(false)).Any(checkpoint => checkpoint.Name == checkpointName))
                throw new VirtualizationException($"The virtual machine '{name}' already has a checkpoint named '{checkpointName}'.");

            await VirshAsync(["snapshot-create-as", "--domain", name, "--name", checkpointName], name, checkpointName, cancellationToken).ConfigureAwait(false);
            return (await ListCheckpointsAsync(name, cancellationToken).ConfigureAwait(false)).FirstOrDefault(checkpoint => checkpoint.Name == checkpointName)
                ?? new VirtualMachineCheckpoint { Name = checkpointName };
        }

        public Task RestoreCheckpointAsync(string name, string checkpointName, CancellationToken cancellationToken = default) =>
            VirshAsync(["snapshot-revert", "--domain", name, "--snapshotname", checkpointName], name, checkpointName, cancellationToken);

        public Task DeleteCheckpointAsync(string name, string checkpointName, CancellationToken cancellationToken = default) =>
            VirshAsync(["snapshot-delete", "--domain", name, "--snapshotname", checkpointName], name, checkpointName, cancellationToken);

        // Runs the command unless the machine is already in the target state; any other state than the allowed ones fails.
        private async Task TransitionAsync(string name, VirtualMachineState target, VirtualMachineState[] allowed, string[] command, CancellationToken cancellationToken)
        {
            var machine = await this.GetAsync(name, cancellationToken).ConfigureAwait(false);
            if (machine.State == target)
                return;
            if (!allowed.Contains(machine.State))
                throw new InvalidVirtualMachineStateException(name, $"The virtual machine '{name}' is {machine.State}.");

            await VirshAsync(command, name, null, cancellationToken).ConfigureAwait(false);
        }

        private async Task CreateDiskAsync(string path, long sizeBytes, CancellationToken cancellationToken)
        {
            string[] arguments = ["create", "-f", LibvirtDomainXml.DiskFormat(path), path, sizeBytes.ToString(CultureInfo.InvariantCulture)];
            var result = await runner.RunAsync(QemuImg, arguments, Environment, cancellationToken).ConfigureAwait(false)
                ?? throw new HypervisorUnavailableException(HypervisorPlatform.KVM, "qemu-img was not found; install the QEMU utilities to create disks.");
            if (result.ExitCode != 0)
                throw new VirtualizationException($"Could not create the disk '{path}': {result.StandardError.Trim()}");
        }

        private async Task<string> VirshAsync(string[] command, string? machine, string? checkpoint, CancellationToken cancellationToken)
        {
            string[] arguments = ["--connect", ConnectionUri, "--quiet", .. command];
            var result = await runner.RunAsync(Virsh, arguments, Environment, cancellationToken).ConfigureAwait(false)
                ?? throw new HypervisorUnavailableException(HypervisorPlatform.KVM, "virsh was not found; install the libvirt client tools.");

            return result.ExitCode == 0 ? result.StandardOutput : throw Classify(result.StandardError.Trim(), machine, checkpoint);
        }

        internal static VirtualizationException Classify(string error, string? machine, string? checkpoint)
        {
            if (error.Contains("snapshot not found", StringComparison.OrdinalIgnoreCase))
                return new CheckpointNotFoundException(machine ?? string.Empty, checkpoint ?? string.Empty);
            if (error.Contains("Domain not found", StringComparison.OrdinalIgnoreCase) || error.Contains("failed to get domain", StringComparison.OrdinalIgnoreCase))
                return new VirtualMachineNotFoundException(machine ?? string.Empty);
            if (error.Contains("failed to connect", StringComparison.OrdinalIgnoreCase))
            {
                var hint = error.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) ? " Run as root or join the 'libvirt' group." : string.Empty;
                return new HypervisorUnavailableException(HypervisorPlatform.KVM, error + hint);
            }
            if (error.Contains("Requested operation is not valid", StringComparison.OrdinalIgnoreCase))
                return new InvalidVirtualMachineStateException(machine ?? string.Empty, error);

            return new VirtualizationException(error.Length > 0 ? error : "virsh failed without an error message.");
        }

        internal static VirtualMachine ParseDomainInfo(string output)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in Lines(output))
            {
                var separator = line.IndexOf(':');
                if (separator > 0)
                    values.TryAdd(line[..separator].Trim(), line[(separator + 1)..].Trim());
            }

            var state = MapState(values.GetValueOrDefault("State"));
            if (state == VirtualMachineState.Off && values.GetValueOrDefault("Managed save") == "yes")
                state = VirtualMachineState.Saved;

            return new VirtualMachine
            {
                Id = values.GetValueOrDefault("UUID") ?? string.Empty,
                Name = values.GetValueOrDefault("Name") ?? string.Empty,
                State = state,
                ProcessorCount = int.TryParse(values.GetValueOrDefault("CPU(s)"), NumberStyles.None, CultureInfo.InvariantCulture, out var processors) ? processors : 0,
                MemoryBytes = ParseKibibytes(values.GetValueOrDefault("Max memory"))
            };
        }

        // virsh domstate / dominfo states.
        internal static VirtualMachineState MapState(string? state) => state switch
        {
            "running" or "idle" or "blocked" => VirtualMachineState.Running,
            "paused" or "pmsuspended" => VirtualMachineState.Paused,
            "in shutdown" or "dying" => VirtualMachineState.Stopping,
            "shut off" => VirtualMachineState.Off,
            "crashed" => VirtualMachineState.Crashed,
            _ => VirtualMachineState.Unknown
        };

        // " Name    Creation Time               State"
        // "---------------------------------------------------"   (both omitted with --quiet)
        // " clean   2024-05-01 10:00:00 +0200   shutoff"
        internal static IReadOnlyList<VirtualMachineCheckpoint> ParseSnapshotList(string output)
        {
            var checkpoints = new List<VirtualMachineCheckpoint>();
            foreach (var line in Lines(output))
            {
                if (line.StartsWith('-'))
                    continue;

                var columns = ColumnSeparator().Split(line);
                if (columns.Length < 2 || (columns[0] == "Name" && columns[1] == "Creation Time"))
                    continue;

                checkpoints.Add(new VirtualMachineCheckpoint { Name = columns[0], CreatedAt = ParseCreationTime(columns[1]) });
            }
            return checkpoints;
        }

        internal static DateTimeOffset? ParseCreationTime(string text)
        {
            // "+0200" -> "+02:00", the form DateTimeOffset understands.
            var normalized = OffsetWithoutColon().Replace(text.Trim(), "$1:$2");
            return DateTimeOffset.TryParseExact(normalized, "yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture, DateTimeStyles.None, out var created) ? created : null;
        }

        private static long ParseKibibytes(string? text)
        {
            var number = text?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return long.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var kibibytes) ? kibibytes * 1024 : 0;
        }

        private static IEnumerable<string> Lines(string text) =>
            text.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0);

        [GeneratedRegex(@"\s{2,}")]
        private static partial Regex ColumnSeparator();

        [GeneratedRegex(@"([+-]\d{2})(\d{2})$")]
        private static partial Regex OffsetWithoutColon();
    }
}

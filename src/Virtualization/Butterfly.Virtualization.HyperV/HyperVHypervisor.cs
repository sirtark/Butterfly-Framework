using Butterfly.Virtualization.Native;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Butterfly.Virtualization.HyperV
{
    /// <summary>
    /// Manages Hyper-V virtual machines through the Hyper-V PowerShell module. The process must run elevated or as a
    /// member of the "Hyper-V Administrators" group, on a Windows edition with the Hyper-V feature enabled.
    /// </summary>
    public sealed class HyperVHypervisor : IHypervisor
    {
        private const string PowerShell = "powershell.exe";

        private readonly ICommandRunner runner;

        public HyperVHypervisor() : this(ProcessCommandRunner.Instance)
        { }
        internal HyperVHypervisor(ICommandRunner runner)
        {
            this.runner = runner;
        }

        public HypervisorPlatform Platform => HypervisorPlatform.HyperV;

        public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
        {
            if (!OperatingSystem.IsWindows())
                return false;
            try
            {
                await RunAsync(HyperVScripts.IsAvailable(), null, null, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (VirtualizationException)
            {
                return false;
            }
        }

        public async Task<IReadOnlyList<VirtualMachine>> ListAsync(CancellationToken cancellationToken = default)
        {
            using var json = JsonDocument.Parse(await RunAsync(HyperVScripts.List(), null, null, cancellationToken).ConfigureAwait(false));
            return [.. json.RootElement.EnumerateArray().Select(ReadMachine)];
        }

        public async Task<VirtualMachine?> FindAsync(string name, CancellationToken cancellationToken = default)
        {
            using var json = JsonDocument.Parse(await RunAsync(HyperVScripts.Find(name), name, null, cancellationToken).ConfigureAwait(false));
            return json.RootElement.ValueKind == JsonValueKind.Null ? null : ReadMachine(json.RootElement);
        }

        public async Task<VirtualMachine> CreateAsync(VirtualMachineSpec spec, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(spec);
            spec.EnsureValid();

            using var json = JsonDocument.Parse(await RunAsync(HyperVScripts.Create(spec), spec.Name, null, cancellationToken).ConfigureAwait(false));
            return ReadMachine(json.RootElement);
        }

        public Task DeleteAsync(string name, CancellationToken cancellationToken = default) => RunAsync(HyperVScripts.Delete(name), name, null, cancellationToken);
        public Task StartAsync(string name, CancellationToken cancellationToken = default) => RunAsync(HyperVScripts.Start(name), name, null, cancellationToken);
        public Task ShutdownAsync(string name, CancellationToken cancellationToken = default) => RunAsync(HyperVScripts.Shutdown(name), name, null, cancellationToken);
        public Task TurnOffAsync(string name, CancellationToken cancellationToken = default) => RunAsync(HyperVScripts.TurnOff(name), name, null, cancellationToken);
        public Task PauseAsync(string name, CancellationToken cancellationToken = default) => RunAsync(HyperVScripts.Pause(name), name, null, cancellationToken);
        public Task ResumeAsync(string name, CancellationToken cancellationToken = default) => RunAsync(HyperVScripts.Resume(name), name, null, cancellationToken);
        public Task SaveAsync(string name, CancellationToken cancellationToken = default) => RunAsync(HyperVScripts.Save(name), name, null, cancellationToken);

        public async Task<IReadOnlyList<VirtualMachineCheckpoint>> ListCheckpointsAsync(string name, CancellationToken cancellationToken = default)
        {
            using var json = JsonDocument.Parse(await RunAsync(HyperVScripts.ListCheckpoints(name), name, null, cancellationToken).ConfigureAwait(false));
            return [.. json.RootElement.EnumerateArray().Select(ReadCheckpoint)];
        }

        public async Task<VirtualMachineCheckpoint> CreateCheckpointAsync(string name, string checkpointName, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(checkpointName);
            using var json = JsonDocument.Parse(await RunAsync(HyperVScripts.CreateCheckpoint(name, checkpointName), name, checkpointName, cancellationToken).ConfigureAwait(false));
            return ReadCheckpoint(json.RootElement);
        }

        public Task RestoreCheckpointAsync(string name, string checkpointName, CancellationToken cancellationToken = default) =>
            RunAsync(HyperVScripts.RestoreCheckpoint(name, checkpointName), name, checkpointName, cancellationToken);

        public Task DeleteCheckpointAsync(string name, string checkpointName, CancellationToken cancellationToken = default) =>
            RunAsync(HyperVScripts.DeleteCheckpoint(name, checkpointName), name, checkpointName, cancellationToken);

        private async Task<string> RunAsync(string body, string? machine, string? checkpoint, CancellationToken cancellationToken)
        {
            // -EncodedCommand passes the script as UTF-16 Base64: no command line quoting, and it is not a script
            // file, so the execution policy does not apply.
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(HyperVScripts.Wrap(body)));
            var result = await runner.RunAsync(PowerShell, ["-NoProfile", "-NonInteractive", "-EncodedCommand", encoded], null, cancellationToken).ConfigureAwait(false)
                ?? throw new HypervisorUnavailableException(HypervisorPlatform.HyperV, "Windows PowerShell (powershell.exe) was not found.");

            var error = CleanError(result.StandardError);
            return result.ExitCode switch
            {
                0 => result.StandardOutput.Trim().TrimStart('﻿'),
                HyperVScripts.ModuleMissing => throw new HypervisorUnavailableException(HypervisorPlatform.HyperV,
                    "The Hyper-V PowerShell module is not installed. Enable the Hyper-V feature (Microsoft-Hyper-V-All)."),
                HyperVScripts.MachineNotFound => throw new VirtualMachineNotFoundException(machine ?? string.Empty),
                HyperVScripts.CheckpointNotFound => throw new CheckpointNotFoundException(machine ?? string.Empty, checkpoint ?? string.Empty),
                HyperVScripts.MachineAlreadyExists => throw new VirtualMachineAlreadyExistsException(machine ?? string.Empty),
                HyperVScripts.InvalidState => throw new InvalidVirtualMachineStateException(machine ?? string.Empty, error),
                HyperVScripts.CheckpointAlreadyExists => throw new VirtualizationException($"The virtual machine '{machine}' already has a checkpoint named '{checkpoint}'."),
                _ => throw Classify(error)
            };
        }

        // The Virtual Machine Management service and its permission errors are reported as plain messages.
        private static VirtualizationException Classify(string error)
        {
            if (error.Contains("permission", StringComparison.OrdinalIgnoreCase) || error.Contains("access is denied", StringComparison.OrdinalIgnoreCase))
                return new HypervisorUnavailableException(HypervisorPlatform.HyperV,
                    $"Not allowed to manage Hyper-V: run elevated or join the 'Hyper-V Administrators' group. {error}");
            if (error.Contains("Virtual Machine Management", StringComparison.OrdinalIgnoreCase))
                return new HypervisorUnavailableException(HypervisorPlatform.HyperV, error);

            return new VirtualizationException(error.Length > 0 ? error : "Hyper-V failed without an error message.");
        }

        // Windows PowerShell serializes progress records as CLIXML on a redirected error stream.
        internal static string CleanError(string error) => string.Join('\n', error
            .Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.Length > 0 && !line.StartsWith("#< CLIXML", StringComparison.Ordinal) && !line.StartsWith("<Objs", StringComparison.Ordinal)))
            .Trim();

        internal static VirtualMachine ReadMachine(JsonElement element)
        {
            var state = MapState(element.GetProperty("State").GetString());
            var uptime = element.TryGetProperty("UptimeSeconds", out var seconds) && seconds.ValueKind == JsonValueKind.Number ? seconds.GetDouble() : 0;

            return new VirtualMachine
            {
                Id = element.GetProperty("Id").GetString() ?? string.Empty,
                Name = element.GetProperty("Name").GetString() ?? string.Empty,
                State = state,
                ProcessorCount = element.GetProperty("ProcessorCount").GetInt32(),
                MemoryBytes = element.GetProperty("MemoryBytes").GetInt64(),
                Uptime = state is VirtualMachineState.Running or VirtualMachineState.Paused && uptime > 0 ? TimeSpan.FromSeconds(uptime) : null
            };
        }

        internal static VirtualMachineCheckpoint ReadCheckpoint(JsonElement element) => new()
        {
            Name = element.GetProperty("Name").GetString() ?? string.Empty,
            CreatedAt = DateTimeOffset.TryParse(element.GetProperty("CreatedAt").GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var created) ? created : null
        };

        // Microsoft.HyperV.PowerShell.VMState; the "...Critical" variants mean the storage of the machine is unreachable.
        internal static VirtualMachineState MapState(string? state) => state?.Replace("Critical", string.Empty, StringComparison.Ordinal) switch
        {
            "Running" => VirtualMachineState.Running,
            "Off" => VirtualMachineState.Off,
            "Starting" or "Resuming" or "Reset" or "ForceReboot" => VirtualMachineState.Starting,
            "Stopping" or "ForceShutdown" => VirtualMachineState.Stopping,
            "Paused" or "Pausing" => VirtualMachineState.Paused,
            "Saving" or "FastSaving" => VirtualMachineState.Saving,
            "Saved" or "FastSaved" or "Hibernated" => VirtualMachineState.Saved,
            _ => VirtualMachineState.Unknown
        };
    }
}

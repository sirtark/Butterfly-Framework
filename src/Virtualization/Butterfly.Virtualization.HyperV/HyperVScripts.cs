using System.Globalization;
using System.Text;

namespace Butterfly.Virtualization.HyperV
{
    // Builds the PowerShell scripts run against the Hyper-V module. Every value is embedded as a single-quoted
    // literal (see Quote), and machines are resolved by exact name: Hyper-V cmdlets treat -Name as a wildcard pattern.
    internal static class HyperVScripts
    {
        // Exit codes of the prologue helpers, mapped to exceptions by HyperVHypervisor.
        public const int ModuleMissing = 2;
        public const int MachineNotFound = 3;
        public const int CheckpointNotFound = 4;
        public const int MachineAlreadyExists = 5;
        public const int InvalidState = 6;
        public const int CheckpointAlreadyExists = 7;

        private const string Prologue = """
            $ErrorActionPreference = 'Stop'
            $ProgressPreference = 'SilentlyContinue'
            [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false
            if (-not (Get-Command Get-VM -ErrorAction SilentlyContinue)) { exit 2 }
            function Get-ButterflyVM([string] $Name) {
                $found = @(Get-VM | Where-Object { $_.Name -eq $Name })
                if ($found.Count -eq 0) { exit 3 }
                if ($found.Count -gt 1) { throw "There are $($found.Count) virtual machines named '$Name'." }
                $found[0]
            }
            function Get-ButterflyCheckpoint($VM, [string] $Name) {
                $found = @(Get-VMSnapshot -VM $VM | Where-Object { $_.Name -eq $Name })
                if ($found.Count -eq 0) { exit 4 }
                $found[0]
            }
            function Assert-ButterflyState($VM, [string[]] $Allowed) {
                if ($Allowed -notcontains $VM.State.ToString()) {
                    [Console]::Error.WriteLine("The virtual machine '$($VM.Name)' is $($VM.State).")
                    exit 6
                }
            }
            function ConvertTo-ButterflyVM($VM) {
                [ordered]@{ Id = $VM.VMId.ToString(); Name = $VM.Name; State = $VM.State.ToString(); ProcessorCount = $VM.ProcessorCount; MemoryBytes = $VM.MemoryStartup; UptimeSeconds = $VM.Uptime.TotalSeconds }
            }
            function ConvertTo-ButterflyCheckpoint($Checkpoint) {
                [ordered]@{ Name = $Checkpoint.Name; CreatedAt = $Checkpoint.CreationTime.ToUniversalTime().ToString('o') }
            }
            function Write-ButterflyJson($Value) { ConvertTo-Json -InputObject $Value -Compress -Depth 4 }
            """;

        public static string Wrap(string body)
        {
            var script = new StringBuilder(Prologue).AppendLine();
            script.AppendLine("try {");
            foreach (var line in body.Split('\n'))
                script.Append("    ").AppendLine(line.TrimEnd('\r'));
            script.AppendLine("} catch {");
            script.AppendLine("    [Console]::Error.WriteLine($_.Exception.Message)");
            script.AppendLine("    exit 1");
            script.AppendLine("}");
            return script.ToString();
        }

        public static string IsAvailable() => "$null = Get-VMHost\n'true'";

        public static string List() => "Write-ButterflyJson @(Get-VM | ForEach-Object { ConvertTo-ButterflyVM $_ })";

        public static string Find(string name) => $$"""
            $found = @(Get-VM | Where-Object { $_.Name -eq {{Quote(name)}} })
            if ($found.Count -eq 0) { 'null' } else { Write-ButterflyJson (ConvertTo-ButterflyVM $found[0]) }
            """;

        public static string Start(string name) => OnMachine(name, "Running", ["Off", "Saved"], "Start-VM -VM $vm");
        // Stop-VM without -TurnOff asks the guest to shut down through the integration services.
        public static string Shutdown(string name) => OnMachine(name, "Off", ["Running"], "Stop-VM -VM $vm -Force");
        public static string TurnOff(string name) => $"$vm = Get-ButterflyVM {Quote(name)}\nif ($vm.State -ne 'Off') {{ Stop-VM -VM $vm -TurnOff -Force }}";
        public static string Pause(string name) => OnMachine(name, "Paused", ["Running"], "Suspend-VM -VM $vm");
        public static string Resume(string name) => OnMachine(name, "Running", ["Paused"], "Resume-VM -VM $vm");
        public static string Save(string name) => OnMachine(name, "Saved", ["Running", "Paused"], "Save-VM -VM $vm");

        public static string Delete(string name) => $"""
            $vm = Get-ButterflyVM {Quote(name)}
            if ($vm.State -ne 'Off' -and $vm.State -ne 'Saved') {"{"} Stop-VM -VM $vm -TurnOff -Force {"}"}
            Remove-VM -VM $vm -Force
            """;

        public static string ListCheckpoints(string name) => $"""
            $vm = Get-ButterflyVM {Quote(name)}
            Write-ButterflyJson @(Get-VMSnapshot -VM $vm | ForEach-Object {"{"} ConvertTo-ButterflyCheckpoint $_ {"}"})
            """;

        public static string CreateCheckpoint(string name, string checkpoint) => $$"""
            $vm = Get-ButterflyVM {{Quote(name)}}
            if (@(Get-VMSnapshot -VM $vm | Where-Object { $_.Name -eq {{Quote(checkpoint)}} }).Count -gt 0) { exit 7 }
            $checkpoint = Checkpoint-VM -VM $vm -SnapshotName {{Quote(checkpoint)}} -Passthru
            Write-ButterflyJson (ConvertTo-ButterflyCheckpoint $checkpoint)
            """;

        public static string RestoreCheckpoint(string name, string checkpoint) => $"""
            $vm = Get-ButterflyVM {Quote(name)}
            $checkpoint = Get-ButterflyCheckpoint $vm {Quote(checkpoint)}
            Restore-VMSnapshot -VMSnapshot $checkpoint -Confirm:$false
            """;

        public static string DeleteCheckpoint(string name, string checkpoint) => $"""
            $vm = Get-ButterflyVM {Quote(name)}
            $checkpoint = Get-ButterflyCheckpoint $vm {Quote(checkpoint)}
            Remove-VMSnapshot -VMSnapshot $checkpoint -Confirm:$false
            """;

        public static string Create(VirtualMachineSpec spec)
        {
            var generation = spec.Firmware == VirtualMachineFirmware.Uefi ? 2 : 1;
            var script = new StringBuilder();
            script.AppendLine($"if (@(Get-VM | Where-Object {{ $_.Name -eq {Quote(spec.Name)} }}).Count -gt 0) {{ exit {MachineAlreadyExists} }}");
            script.AppendLine("$vm = $null");
            script.AppendLine("try {");

            script.Append($"    $vm = New-VM -Name {Quote(spec.Name)} -Generation {generation} -MemoryStartupBytes {Number(spec.MemoryBytes)} -NoVHD");
            if (spec.Network is not null)
                script.Append($" -SwitchName {Quote(spec.Network)}");
            script.AppendLine();
            script.AppendLine($"    Set-VMProcessor -VM $vm -Count {Number(spec.ProcessorCount)}");

            foreach (var disk in spec.Disks)
            {
                if (disk.SizeBytes is { } size)
                    script.AppendLine($"    if (-not (Test-Path -LiteralPath {Quote(disk.Path)})) {{ New-VHD -Path {Quote(disk.Path)} -SizeBytes {Number(size)} -Dynamic | Out-Null }}");
                script.AppendLine($"    Add-VMHardDiskDrive -VM $vm -Path {Quote(disk.Path)}");
            }

            if (spec.InstallationMedia is not null)
            {
                // Generation 1 machines come with an empty DVD drive on the IDE controller.
                if (generation == 1)
                    script.AppendLine($"    $dvd = @(Get-VMDvdDrive -VM $vm)[0]; if ($dvd) {{ Set-VMDvdDrive -VMDvdDrive $dvd -Path {Quote(spec.InstallationMedia)} }} else {{ Add-VMDvdDrive -VM $vm -Path {Quote(spec.InstallationMedia)} }}");
                else
                    script.AppendLine($"    $dvd = Add-VMDvdDrive -VM $vm -Path {Quote(spec.InstallationMedia)} -Passthru");
            }

            if (generation == 2)
            {
                script.Append($"    Set-VMFirmware -VM $vm -EnableSecureBoot {(spec.SecureBoot ? "On" : "Off")}");
                if (spec.InstallationMedia is not null)
                    script.Append(" -FirstBootDevice $dvd");
                else if (spec.Disks.Count > 0)
                    script.Append(" -FirstBootDevice @(Get-VMHardDiskDrive -VM $vm)[0]");
                script.AppendLine();
            }

            // A half-created machine is removed: the operation either succeeds or leaves nothing behind.
            script.AppendLine("} catch {");
            script.AppendLine("    if ($vm) { Remove-VM -VM $vm -Force -ErrorAction SilentlyContinue }");
            script.AppendLine("    throw");
            script.AppendLine("}");
            script.Append("Write-ButterflyJson (ConvertTo-ButterflyVM (Get-VM -Id $vm.VMId))");
            return script.ToString();
        }

        // Runs the command unless the machine is already in the target state; any other state than the allowed ones fails.
        private static string OnMachine(string name, string doneState, string[] allowedStates, string command) => $"""
            $vm = Get-ButterflyVM {Quote(name)}
            if ($vm.State -eq '{doneState}') {"{"} return {"}"}
            Assert-ButterflyState $vm @({string.Join(", ", allowedStates.Select(state => $"'{state}'"))})
            {command}
            """;

        // PowerShell single-quoted literal: nothing inside is expanded. PowerShell also accepts the typographic
        // quotes (U+2018..U+201B) as single quotes, so they are doubled too.
        internal static string Quote(string value)
        {
            var literal = new StringBuilder(value.Length + 2).Append('\'');
            foreach (var character in value)
            {
                literal.Append(character);
                if (character is '\'' or '‘' or '’' or '‚' or '‛')
                    literal.Append(character);
            }
            return literal.Append('\'').ToString();
        }

        private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    }
}

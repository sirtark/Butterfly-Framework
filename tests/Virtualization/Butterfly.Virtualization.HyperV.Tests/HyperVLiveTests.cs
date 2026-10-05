using Butterfly.Virtualization.Native;

namespace Butterfly.Virtualization.HyperV.Tests
{
    // Runs against the real Windows PowerShell of the machine. They do not need Hyper-V: without it they check that
    // the provider reports it as unavailable instead of failing.
    public class HyperVLiveTests
    {
        public static TheoryData<string, string> Scripts()
        {
            var spec = new VirtualMachineSpec
            {
                Name = "it's [complex] $(name)",
                ProcessorCount = 2,
                MemoryBytes = 1024L * 1024 * 1024,
                SecureBoot = true,
                Network = "Default Switch",
                Disks = [new VirtualDiskSpec { Path = @"C:\VMs\a.vhdx", SizeBytes = 1024L * 1024 * 1024 }, new VirtualDiskSpec { Path = @"C:\VMs\b.vhdx" }],
                InstallationMedia = @"C:\ISO\setup.iso"
            };

            return new TheoryData<string, string>
            {
                { "IsAvailable", HyperVScripts.IsAvailable() },
                { "List", HyperVScripts.List() },
                { "Find", HyperVScripts.Find(spec.Name) },
                { "CreateUefi", HyperVScripts.Create(spec) },
                { "CreateBios", HyperVScripts.Create(spec with { Firmware = VirtualMachineFirmware.Bios, SecureBoot = false, Network = null }) },
                { "Delete", HyperVScripts.Delete(spec.Name) },
                { "Start", HyperVScripts.Start(spec.Name) },
                { "Shutdown", HyperVScripts.Shutdown(spec.Name) },
                { "TurnOff", HyperVScripts.TurnOff(spec.Name) },
                { "Pause", HyperVScripts.Pause(spec.Name) },
                { "Resume", HyperVScripts.Resume(spec.Name) },
                { "Save", HyperVScripts.Save(spec.Name) },
                { "ListCheckpoints", HyperVScripts.ListCheckpoints(spec.Name) },
                { "CreateCheckpoint", HyperVScripts.CreateCheckpoint(spec.Name, "it's") },
                { "RestoreCheckpoint", HyperVScripts.RestoreCheckpoint(spec.Name, "it's") },
                { "DeleteCheckpoint", HyperVScripts.DeleteCheckpoint(spec.Name, "it's") }
            };
        }

        [Theory]
        [MemberData(nameof(Scripts))]
        public async Task ScriptsAreValidPowerShell(string operation, string body)
        {
            if (!OperatingSystem.IsWindows())
                return;

            // The parser reports syntax errors without running anything.
            var script = HyperVScripts.Wrap(body);
            var check = $$"""
                $errors = $null
                [void][System.Management.Automation.Language.Parser]::ParseInput({{HyperVScripts.Quote(script)}}, [ref]$null, [ref]$errors)
                $errors | ForEach-Object { $_.Message }
                'PARSED'
                """;
            var result = await ProcessCommandRunner.Instance.RunAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", check], null, CancellationToken.None);

            // The sentinel proves the check itself ran: a failure of the check would leave the output empty too.
            Assert.NotNull(result);
            Assert.True(result.ExitCode == 0 && result.StandardOutput.Trim() == "PARSED", $"{operation}: {result.StandardOutput}{result.StandardError}");
        }

        [Fact]
        public async Task ReportsWhetherHyperVIsUsable()
        {
            var hypervisor = new HyperVHypervisor();

            if (await hypervisor.IsAvailableAsync())
                Assert.NotNull(await hypervisor.ListAsync());
            else
                await Assert.ThrowsAsync<HypervisorUnavailableException>(() => hypervisor.ListAsync());
        }
    }
}

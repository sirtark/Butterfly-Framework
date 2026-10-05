using System.Text.Json;

namespace Butterfly.Virtualization.HyperV.Tests
{
    public class HyperVHypervisorTests
    {
        private const string MachineJson = """{"Id":"4ad3e2c1-7c1d-4f2e-9a4b-0d5e6f7a8b9c","Name":"build-agent","State":"Running","ProcessorCount":4,"MemoryBytes":4294967296,"UptimeSeconds":3600.5}""";

        [Fact]
        public async Task ListsMachines()
        {
            var hypervisor = new HyperVHypervisor(FakeCommandRunner.Returning($"[{MachineJson}]"));

            var machine = Assert.Single(await hypervisor.ListAsync());

            Assert.Equal("4ad3e2c1-7c1d-4f2e-9a4b-0d5e6f7a8b9c", machine.Id);
            Assert.Equal("build-agent", machine.Name);
            Assert.Equal(VirtualMachineState.Running, machine.State);
            Assert.Equal(4, machine.ProcessorCount);
            Assert.Equal(4L * 1024 * 1024 * 1024, machine.MemoryBytes);
            Assert.Equal(TimeSpan.FromSeconds(3600.5), machine.Uptime);
        }

        [Fact]
        public async Task AnEmptyHostHasNoMachines() => Assert.Empty(await new HyperVHypervisor(FakeCommandRunner.Returning("[]")).ListAsync());

        [Fact]
        public async Task FindReturnsNullForAMissingMachine() => Assert.Null(await new HyperVHypervisor(FakeCommandRunner.Returning("null\r\n")).FindAsync("missing"));

        [Fact]
        public async Task ToleratesAByteOrderMark() => Assert.NotNull(await new HyperVHypervisor(FakeCommandRunner.Returning("\uFEFF" + MachineJson)).FindAsync("build-agent"));

        [Fact]
        public async Task ResolvesMachinesByExactName()
        {
            var runner = FakeCommandRunner.Returning("");

            await new HyperVHypervisor(runner).StartAsync("web*[1]");

            // -Name would treat * and [ ] as wildcards; the machine is matched with -eq and passed as an object.
            Assert.Contains("Get-ButterflyVM 'web*[1]'", runner.Scripts[0]);
            Assert.Contains("Start-VM -VM $vm", runner.Scripts[0]);
            Assert.DoesNotContain("-Name 'web", runner.Scripts[0]);
        }

        [Theory]
        [InlineData("x'; Remove-VM -Name * -Force; '", "'x''; Remove-VM -Name * -Force; '''")]
        [InlineData("it\u2019s", "'it\u2019\u2019s'")]
        [InlineData("$(Get-Process)", "'$(Get-Process)'")]
        public void QuotesValuesAsLiterals(string value, string expected) => Assert.Equal(expected, HyperVScripts.Quote(value));

        [Fact]
        public async Task BuildsTheCreationScript()
        {
            // Windows paths are only absolute on Windows.
            if (!OperatingSystem.IsWindows())
                return;

            var runner = FakeCommandRunner.Returning(MachineJson.Replace("Running", "Off"));
            var spec = new VirtualMachineSpec
            {
                Name = "build-agent",
                ProcessorCount = 1,
                MemoryBytes = 64L * 1024 * 1024,
                SecureBoot = true,
                Network = "Default Switch",
                Disks = [new VirtualDiskSpec { Path = @"C:\VMs\build-agent.vhdx", SizeBytes = 1024L * 1024 * 1024 }],
                InstallationMedia = @"C:\ISO\windows.iso"
            };

            var machine = await new HyperVHypervisor(runner).CreateAsync(spec);

            var script = runner.Scripts[0];
            Assert.Contains("New-VM -Name 'build-agent' -Generation 2 -MemoryStartupBytes 67108864 -NoVHD -SwitchName 'Default Switch'", script);
            Assert.Contains("Set-VMProcessor -VM $vm -Count 1", script);
            Assert.Contains(@"New-VHD -Path 'C:\VMs\build-agent.vhdx' -SizeBytes 1073741824 -Dynamic", script);
            Assert.Contains(@"Add-VMHardDiskDrive -VM $vm -Path 'C:\VMs\build-agent.vhdx'", script);
            Assert.Contains(@"$dvd = Add-VMDvdDrive -VM $vm -Path 'C:\ISO\windows.iso' -Passthru", script);
            Assert.Contains("Set-VMFirmware -VM $vm -EnableSecureBoot On -FirstBootDevice $dvd", script);
            Assert.Contains("Remove-VM -VM $vm -Force", script);
            Assert.Equal(VirtualMachineState.Off, machine.State);
        }

        [Fact]
        public async Task BiosMachinesAreGeneration1AndReuseTheirDvdDrive()
        {
            if (!OperatingSystem.IsWindows())
                return;

            var runner = FakeCommandRunner.Returning(MachineJson);
            var spec = new VirtualMachineSpec
            {
                Name = "legacy",
                ProcessorCount = 1,
                MemoryBytes = 64L * 1024 * 1024,
                Firmware = VirtualMachineFirmware.Bios,
                InstallationMedia = @"C:\ISO\dos.iso"
            };

            await new HyperVHypervisor(runner).CreateAsync(spec);

            Assert.Contains("-Generation 1", runner.Scripts[0]);
            Assert.Contains("Set-VMDvdDrive", runner.Scripts[0]);
            Assert.DoesNotContain("Set-VMFirmware", runner.Scripts[0]);
            Assert.DoesNotContain("-SwitchName", runner.Scripts[0]);
        }

        [Fact]
        public async Task AnInvalidSpecificationIsNeverSent()
        {
            var runner = FakeCommandRunner.Returning(MachineJson);

            await Assert.ThrowsAsync<ArgumentException>(() => new HyperVHypervisor(runner).CreateAsync(new VirtualMachineSpec { Name = "bad/name" }));
            Assert.Empty(runner.Scripts);
        }

        [Fact]
        public async Task OperationsSkipMachinesAlreadyInTheTargetState()
        {
            var runner = FakeCommandRunner.Returning("");
            var hypervisor = new HyperVHypervisor(runner);

            await hypervisor.PauseAsync("vm");
            await hypervisor.ResumeAsync("vm");
            await hypervisor.SaveAsync("vm");
            await hypervisor.ShutdownAsync("vm");

            Assert.Contains("if ($vm.State -eq 'Paused') { return }", runner.Scripts[0]);
            Assert.Contains("Assert-ButterflyState $vm @('Running')", runner.Scripts[0]);
            Assert.Contains("if ($vm.State -eq 'Running') { return }", runner.Scripts[1]);
            Assert.Contains("Assert-ButterflyState $vm @('Running', 'Paused')", runner.Scripts[2]);
            Assert.Contains("Stop-VM -VM $vm -Force", runner.Scripts[3]);
            Assert.DoesNotContain("-TurnOff", runner.Scripts[3]);
        }

        [Fact]
        public async Task MapsExitCodesToExceptions()
        {
            static HyperVHypervisor Failing(int exitCode, string error = "") => new(FakeCommandRunner.Returning("", exitCode, error));

            var unavailable = await Assert.ThrowsAsync<HypervisorUnavailableException>(() => Failing(2).ListAsync());
            Assert.Contains("Microsoft-Hyper-V-All", unavailable.Message);
            Assert.Equal("vm", (await Assert.ThrowsAsync<VirtualMachineNotFoundException>(() => Failing(3).StartAsync("vm"))).Name);
            Assert.Equal("clean", (await Assert.ThrowsAsync<CheckpointNotFoundException>(() => Failing(4).RestoreCheckpointAsync("vm", "clean"))).Checkpoint);
            Assert.Contains("is Off", (await Assert.ThrowsAsync<InvalidVirtualMachineStateException>(() => Failing(6, "The virtual machine 'vm' is Off.").PauseAsync("vm"))).Message);
            await Assert.ThrowsAsync<VirtualizationException>(() => Failing(7).CreateCheckpointAsync("vm", "clean"));
            await Assert.ThrowsAsync<HypervisorUnavailableException>(() => Failing(1, "You do not have the required permission to complete this task.").ListAsync());
            Assert.Equal("Disk is full.", (await Assert.ThrowsAsync<VirtualizationException>(() => Failing(1, "#< CLIXML\r\n<Objs Version=\"1.1.0.1\"></Objs>\r\nDisk is full.\r\n").StartAsync("vm"))).Message);
        }

        [Fact]
        public async Task MissingPowerShellMeansUnavailable()
        {
            var hypervisor = new HyperVHypervisor(new FakeCommandRunner(null));

            await Assert.ThrowsAsync<HypervisorUnavailableException>(() => hypervisor.ListAsync());
            Assert.False(await hypervisor.IsAvailableAsync());
        }

        [Fact]
        public void ReadsCheckpoints()
        {
            using var json = JsonDocument.Parse("""{"Name":"before update","CreatedAt":"2026-10-04T15:30:00.0000000Z"}""");

            var checkpoint = HyperVHypervisor.ReadCheckpoint(json.RootElement);

            Assert.Equal("before update", checkpoint.Name);
            Assert.Equal(new DateTimeOffset(2026, 10, 4, 15, 30, 0, TimeSpan.Zero), checkpoint.CreatedAt);
        }

        [Theory]
        [InlineData("Running", VirtualMachineState.Running)]
        [InlineData("RunningCritical", VirtualMachineState.Running)]
        [InlineData("Off", VirtualMachineState.Off)]
        [InlineData("Saved", VirtualMachineState.Saved)]
        [InlineData("FastSaved", VirtualMachineState.Saved)]
        [InlineData("Paused", VirtualMachineState.Paused)]
        [InlineData("Starting", VirtualMachineState.Starting)]
        [InlineData("Stopping", VirtualMachineState.Stopping)]
        [InlineData("Saving", VirtualMachineState.Saving)]
        [InlineData("Other", VirtualMachineState.Unknown)]
        [InlineData(null, VirtualMachineState.Unknown)]
        public void MapsHyperVStates(string? state, VirtualMachineState expected) => Assert.Equal(expected, HyperVHypervisor.MapState(state));
    }
}

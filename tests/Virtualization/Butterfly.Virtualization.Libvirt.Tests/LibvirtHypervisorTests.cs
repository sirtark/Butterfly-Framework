using Butterfly.Virtualization.Native;
using System.Xml.Linq;

namespace Butterfly.Virtualization.Libvirt.Tests
{
    public class LibvirtHypervisorTests
    {
        private const string RunningInfo = """
            Id:             3
            Name:           build-agent
            UUID:           c7a5fdbd-cdaf-9455-926a-d65c16db1809
            OS Type:        hvm
            State:          running
            CPU(s):         4
            CPU time:       12.3s
            Max memory:     4194304 KiB
            Used memory:    4194304 KiB
            Persistent:     yes
            Autostart:      disable
            Managed save:   no
            """;

        // Answers virsh by command name (the first argument after the connection options).
        private sealed class FakeVirsh : ICommandRunner
        {
            public Dictionary<string, CommandResult> Answers { get; } = [];
            public List<string> Commands { get; } = [];

            public FakeVirsh Answer(string command, string output, int exitCode = 0, string error = "")
            {
                Answers[command] = new CommandResult(exitCode, output, error);
                return this;
            }

            public Task<CommandResult?> RunAsync(string fileName, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? environment, CancellationToken cancellationToken)
            {
                Assert.Equal("C", environment?["LC_ALL"]);
                if (fileName == "qemu-img")
                {
                    Commands.Add("qemu-img " + string.Join(' ', arguments));
                    return Task.FromResult<CommandResult?>(new CommandResult(0, "", ""));
                }

                Assert.Equal("virsh", fileName);
                Assert.Equal(["--connect", "qemu:///system", "--quiet"], arguments.Take(3));
                var command = arguments[3];
                Commands.Add(string.Join(' ', arguments.Skip(3)));
                return Task.FromResult<CommandResult?>(Answers.TryGetValue(command, out var answer) ? answer : new CommandResult(0, "", ""));
            }
        }

        private static LibvirtHypervisor Create(FakeVirsh virsh, bool kvm = true, bool x86 = true) =>
            new(LibvirtHypervisor.SystemConnection, virsh, () => kvm, x86);

        [Fact]
        public void ParsesDomainInfo()
        {
            var machine = LibvirtHypervisor.ParseDomainInfo(RunningInfo);

            Assert.Equal("c7a5fdbd-cdaf-9455-926a-d65c16db1809", machine.Id);
            Assert.Equal("build-agent", machine.Name);
            Assert.Equal(VirtualMachineState.Running, machine.State);
            Assert.Equal(4, machine.ProcessorCount);
            Assert.Equal(4L * 1024 * 1024 * 1024, machine.MemoryBytes);
            Assert.Null(machine.Uptime);
        }

        [Fact]
        public void AStoppedMachineWithManagedSaveIsSaved()
        {
            var info = RunningInfo.Replace("running", "shut off").Replace("Managed save:   no", "Managed save:   yes");

            Assert.Equal(VirtualMachineState.Saved, LibvirtHypervisor.ParseDomainInfo(info).State);
        }

        [Theory]
        [InlineData("running", VirtualMachineState.Running)]
        [InlineData("idle", VirtualMachineState.Running)]
        [InlineData("paused", VirtualMachineState.Paused)]
        [InlineData("in shutdown", VirtualMachineState.Stopping)]
        [InlineData("shut off", VirtualMachineState.Off)]
        [InlineData("crashed", VirtualMachineState.Crashed)]
        [InlineData("no state", VirtualMachineState.Unknown)]
        public void MapsDomainStates(string state, VirtualMachineState expected) => Assert.Equal(expected, LibvirtHypervisor.MapState(state));

        [Fact]
        public async Task ListsEveryDomain()
        {
            var virsh = new FakeVirsh().Answer("list", "build-agent\nbuild-agent\n\n").Answer("dominfo", RunningInfo);

            var machines = await Create(virsh).ListAsync();

            Assert.Equal(2, machines.Count);
            Assert.Equal(["list --all --name", "dominfo --domain build-agent", "dominfo --domain build-agent"], virsh.Commands);
        }

        [Fact]
        public async Task FindReturnsNullForAMissingDomain()
        {
            var virsh = new FakeVirsh().Answer("dominfo", "", 1, "error: failed to get domain 'ghost'");

            Assert.Null(await Create(virsh).FindAsync("ghost"));
        }

        [Fact]
        public async Task OperationsSkipMachinesAlreadyInTheTargetState()
        {
            var virsh = new FakeVirsh().Answer("dominfo", RunningInfo);
            var hypervisor = Create(virsh);

            await hypervisor.StartAsync("build-agent");
            await hypervisor.ResumeAsync("build-agent");
            await hypervisor.PauseAsync("build-agent");

            Assert.Equal(["dominfo --domain build-agent", "dominfo --domain build-agent", "dominfo --domain build-agent", "suspend --domain build-agent"], virsh.Commands);
        }

        [Fact]
        public async Task RejectsOperationsThatDoNotFitTheState()
        {
            var virsh = new FakeVirsh().Answer("dominfo", RunningInfo.Replace("running", "shut off"));

            var exception = await Assert.ThrowsAsync<InvalidVirtualMachineStateException>(() => Create(virsh).PauseAsync("build-agent"));

            Assert.Contains("Off", exception.Message);
            Assert.DoesNotContain("suspend --domain build-agent", virsh.Commands);
        }

        [Fact]
        public async Task TurningOffASavedMachineDiscardsItsMemory()
        {
            var virsh = new FakeVirsh().Answer("dominfo", RunningInfo.Replace("running", "shut off").Replace("Managed save:   no", "Managed save:   yes"));

            await Create(virsh).TurnOffAsync("build-agent");

            Assert.Equal("managedsave-remove --domain build-agent", virsh.Commands[^1]);
        }

        [Fact]
        public async Task DeleteTurnsTheMachineOffFirst()
        {
            var virsh = new FakeVirsh().Answer("dominfo", RunningInfo);

            await Create(virsh).DeleteAsync("build-agent");

            Assert.Equal(["dominfo --domain build-agent", "destroy --domain build-agent", "undefine --domain build-agent --managed-save --snapshots-metadata --nvram"], virsh.Commands);
        }

        [Fact]
        public async Task CreateRefusesAnExistingName()
        {
            if (OperatingSystem.IsWindows())
                return;

            var virsh = new FakeVirsh().Answer("dominfo", RunningInfo);

            await Assert.ThrowsAsync<VirtualMachineAlreadyExistsException>(() => Create(virsh).CreateAsync(new VirtualMachineSpec { Name = "build-agent", ProcessorCount = 1, MemoryBytes = 64L * 1024 * 1024 }));
            Assert.DoesNotContain(virsh.Commands, command => command.StartsWith("define", StringComparison.Ordinal));
        }

        [Fact]
        public async Task ArmMachinesNeedUefi() =>
            await Assert.ThrowsAsync<ArgumentException>(() => Create(new FakeVirsh(), x86: false)
                .CreateAsync(new VirtualMachineSpec { Name = "arm", ProcessorCount = 1, MemoryBytes = 64L * 1024 * 1024, Firmware = VirtualMachineFirmware.Bios }));

        [Fact]
        public void ParsesSnapshotLists()
        {
            const string output = """
                 Name            Creation Time               State
                ---------------------------------------------------------
                 clean           2026-10-04 15:30:00 +0200   shutoff
                 before update   2026-10-05 08:00:00 -0300   running
                """;

            var checkpoints = LibvirtHypervisor.ParseSnapshotList(output);

            Assert.Equal(["clean", "before update"], checkpoints.Select(checkpoint => checkpoint.Name));
            Assert.Equal(new DateTimeOffset(2026, 10, 4, 15, 30, 0, TimeSpan.FromHours(2)), checkpoints[0].CreatedAt);
            Assert.Equal(new DateTimeOffset(2026, 10, 5, 8, 0, 0, TimeSpan.FromHours(-3)), checkpoints[1].CreatedAt);
        }

        [Fact]
        public async Task CreatesCheckpointsWithUniqueNames()
        {
            var virsh = new FakeVirsh().Answer("snapshot-list", " clean   2026-10-04 15:30:00 +0200   shutoff\n");

            await Assert.ThrowsAsync<VirtualizationException>(() => Create(virsh).CreateCheckpointAsync("build-agent", "clean"));
            var created = await Create(virsh).CreateCheckpointAsync("build-agent", "fresh");

            Assert.Equal("fresh", created.Name);
            Assert.Contains("snapshot-create-as --domain build-agent --name fresh", virsh.Commands);
        }

        [Theory]
        [InlineData("error: failed to get domain 'vm'\nerror: Domain not found: no domain with matching name 'vm'", typeof(VirtualMachineNotFoundException))]
        [InlineData("error: Domain snapshot not found: no domain snapshot with matching name 'clean'", typeof(CheckpointNotFoundException))]
        [InlineData("error: failed to connect to the hypervisor\nerror: Failed to connect socket to '/var/run/libvirt/libvirt-sock': Permission denied", typeof(HypervisorUnavailableException))]
        [InlineData("error: Requested operation is not valid: domain is not running", typeof(InvalidVirtualMachineStateException))]
        [InlineData("error: something else", typeof(VirtualizationException))]
        public void ClassifiesVirshErrors(string error, Type expected) =>
            Assert.IsType(expected, LibvirtHypervisor.Classify(error, "vm", "clean"));

        [Fact]
        public void ThePermissionHintMentionsTheLibvirtGroup() =>
            Assert.Contains("'libvirt' group", LibvirtHypervisor.Classify("error: failed to connect to the hypervisor ... Permission denied", null, null).Message);

        [Fact]
        public async Task MissingVirshMeansUnavailable()
        {
            var hypervisor = new LibvirtHypervisor(LibvirtHypervisor.SystemConnection, new MissingProgram(), () => true, true);

            Assert.False(await hypervisor.IsAvailableAsync());
            await Assert.ThrowsAsync<HypervisorUnavailableException>(() => hypervisor.ListAsync());
        }

        private sealed class MissingProgram : ICommandRunner
        {
            public Task<CommandResult?> RunAsync(string fileName, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? environment, CancellationToken cancellationToken) =>
                Task.FromResult<CommandResult?>(null);
        }
    }

    public class LibvirtDomainXmlTests
    {
        private static readonly VirtualMachineSpec Spec = new()
        {
            Name = "build-agent",
            ProcessorCount = 4,
            MemoryBytes = 4L * 1024 * 1024 * 1024,
            SecureBoot = true,
            Network = "default",
            Disks = [new VirtualDiskSpec { Path = "/var/lib/libvirt/images/build-agent.qcow2" }, new VirtualDiskSpec { Path = "/data/scratch.img" }],
            InstallationMedia = "/isos/ubuntu.iso"
        };

        [Fact]
        public void DescribesAnX86KvmMachine()
        {
            var domain = LibvirtDomainXml.Build(Spec, kvm: true, x86: true).Root!;

            Assert.Equal("kvm", (string?)domain.Attribute("type"));
            Assert.Equal("build-agent", (string?)domain.Element("name"));
            Assert.Equal("4294967296", (string?)domain.Element("memory"));
            Assert.Equal("bytes", (string?)domain.Element("memory")!.Attribute("unit"));
            Assert.Equal("4", (string?)domain.Element("vcpu"));
            Assert.Equal("host-passthrough", (string?)domain.Element("cpu")!.Attribute("mode"));

            var os = domain.Element("os")!;
            Assert.Equal("efi", (string?)os.Attribute("firmware"));
            Assert.Equal("q35", (string?)os.Element("type")!.Attribute("machine"));
            Assert.Equal(["cdrom", "hd"], os.Elements("boot").Select(boot => (string?)boot.Attribute("dev")));
            Assert.Equal("yes", (string?)os.Element("firmware")!.Elements("feature").Single(feature => (string?)feature.Attribute("name") == "secure-boot").Attribute("enabled"));
            Assert.NotNull(domain.Element("features")!.Element("smm"));

            var disks = domain.Element("devices")!.Elements("disk").ToList();
            Assert.Equal(["vda", "vdb", "sda"], disks.Select(disk => (string?)disk.Element("target")!.Attribute("dev")));
            Assert.Equal(["qcow2", "raw", "raw"], disks.Select(disk => (string?)disk.Element("driver")!.Attribute("type")));
            Assert.Equal("sata", (string?)disks[2].Element("target")!.Attribute("bus"));
            Assert.NotNull(disks[2].Element("readonly"));

            Assert.Equal("default", (string?)domain.Element("devices")!.Element("interface")!.Element("source")!.Attribute("network"));
        }

        [Fact]
        public void EmulatesWithoutKvmAndAdaptsToArm()
        {
            var domain = LibvirtDomainXml.Build(Spec with { SecureBoot = false }, kvm: false, x86: false).Root!;

            Assert.Equal("qemu", (string?)domain.Attribute("type"));
            Assert.Null(domain.Element("cpu"));
            Assert.Equal("virt", (string?)domain.Element("os")!.Element("type")!.Attribute("machine"));
            Assert.Null(domain.Element("features")!.Element("apic"));
            Assert.Null(domain.Element("features")!.Element("smm"));
            Assert.Equal("scsi", (string?)domain.Element("devices")!.Elements("disk").Last().Element("target")!.Attribute("bus"));
            Assert.NotNull(domain.Element("devices")!.Element("controller"));
        }

        [Fact]
        public void ABiosMachineWithoutExtrasIsMinimal()
        {
            var domain = LibvirtDomainXml.Build(new VirtualMachineSpec { Name = "plain", Firmware = VirtualMachineFirmware.Bios }, kvm: true, x86: true).Root!;

            Assert.Null(domain.Element("os")!.Attribute("firmware"));
            Assert.Null(domain.Element("os")!.Element("type")!.Attribute("machine"));
            Assert.Equal(["hd"], domain.Element("os")!.Elements("boot").Select(boot => (string?)boot.Attribute("dev")));
            Assert.Empty(domain.Element("devices")!.Elements("disk"));
            Assert.Null(domain.Element("devices")!.Element("interface"));
        }

        [Fact]
        public void EscapesNames()
        {
            var xml = LibvirtDomainXml.Build(new VirtualMachineSpec { Name = "a<b>&'c" }, kvm: true, x86: true).ToString();

            Assert.Contains("<name>a&lt;b&gt;&amp;'c</name>", xml);
            Assert.Equal("a<b>&'c", (string?)XDocument.Parse(xml).Root!.Element("name"));
        }
    }
}

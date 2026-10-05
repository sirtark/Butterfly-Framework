using Butterfly.Chrysalis;
using Butterfly.Chrysalis.Binary;
using Butterfly.Networking.Sockets;

namespace Butterfly.Virtualization.Chrysalis.Tests
{
    // A hypervisor in memory: the service is tested over the wire without Hyper-V or libvirt.
    internal sealed class MemoryHypervisor : IHypervisor
    {
        private readonly Dictionary<string, VirtualMachine> machines = [];
        private readonly Dictionary<string, List<VirtualMachineCheckpoint>> checkpoints = [];

        public HypervisorPlatform Platform => HypervisorPlatform.KVM;
        public bool Available { get; set; } = true;

        private void EnsureAvailable()
        {
            if (!Available)
                throw new HypervisorUnavailableException(Platform, "The hypervisor is stopped.");
        }

        private VirtualMachine Get(string name) => machines.TryGetValue(name, out var machine) ? machine : throw new VirtualMachineNotFoundException(name);

        private Task Move(string name, VirtualMachineState from, VirtualMachineState to)
        {
            EnsureAvailable();
            var machine = Get(name);
            if (machine.State != from)
                throw new InvalidVirtualMachineStateException(name, $"The virtual machine '{name}' is {machine.State}.");
            machines[name] = machine with { State = to, Uptime = to == VirtualMachineState.Running ? TimeSpan.FromMinutes(90.5) : null };
            return Task.CompletedTask;
        }

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(Available);

        public Task<IReadOnlyList<VirtualMachine>> ListAsync(CancellationToken cancellationToken = default)
        {
            EnsureAvailable();
            return Task.FromResult<IReadOnlyList<VirtualMachine>>([.. machines.Values]);
        }

        public Task<VirtualMachine?> FindAsync(string name, CancellationToken cancellationToken = default) => Task.FromResult(machines.GetValueOrDefault(name));

        public Task<VirtualMachine> CreateAsync(VirtualMachineSpec spec, CancellationToken cancellationToken = default)
        {
            EnsureAvailable();
            spec.EnsureValid();
            if (machines.ContainsKey(spec.Name))
                throw new VirtualMachineAlreadyExistsException(spec.Name);
            var machine = new VirtualMachine { Id = Guid.NewGuid().ToString(), Name = spec.Name, State = VirtualMachineState.Off, ProcessorCount = spec.ProcessorCount, MemoryBytes = spec.MemoryBytes };
            machines[spec.Name] = machine;
            checkpoints[spec.Name] = [];
            return Task.FromResult(machine);
        }

        public Task DeleteAsync(string name, CancellationToken cancellationToken = default)
        {
            Get(name);
            machines.Remove(name);
            return Task.CompletedTask;
        }

        public Task StartAsync(string name, CancellationToken cancellationToken = default) => Move(name, VirtualMachineState.Off, VirtualMachineState.Running);
        public Task ShutdownAsync(string name, CancellationToken cancellationToken = default) => Move(name, VirtualMachineState.Running, VirtualMachineState.Off);
        public Task TurnOffAsync(string name, CancellationToken cancellationToken = default) => Move(name, VirtualMachineState.Running, VirtualMachineState.Off);
        public Task PauseAsync(string name, CancellationToken cancellationToken = default) => Move(name, VirtualMachineState.Running, VirtualMachineState.Paused);
        public Task ResumeAsync(string name, CancellationToken cancellationToken = default) => Move(name, VirtualMachineState.Paused, VirtualMachineState.Running);
        public Task SaveAsync(string name, CancellationToken cancellationToken = default) => Move(name, VirtualMachineState.Running, VirtualMachineState.Saved);

        public Task<IReadOnlyList<VirtualMachineCheckpoint>> ListCheckpointsAsync(string name, CancellationToken cancellationToken = default)
        {
            Get(name);
            return Task.FromResult<IReadOnlyList<VirtualMachineCheckpoint>>([.. checkpoints[name]]);
        }

        public Task<VirtualMachineCheckpoint> CreateCheckpointAsync(string name, string checkpointName, CancellationToken cancellationToken = default)
        {
            Get(name);
            var checkpoint = new VirtualMachineCheckpoint { Name = checkpointName, CreatedAt = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero) };
            checkpoints[name].Add(checkpoint);
            return Task.FromResult(checkpoint);
        }

        public Task RestoreCheckpointAsync(string name, string checkpointName, CancellationToken cancellationToken = default)
        {
            Get(name);
            return checkpoints[name].Any(checkpoint => checkpoint.Name == checkpointName) ? Task.CompletedTask : throw new CheckpointNotFoundException(name, checkpointName);
        }

        public Task DeleteCheckpointAsync(string name, string checkpointName, CancellationToken cancellationToken = default)
        {
            Get(name);
            return checkpoints[name].RemoveAll(checkpoint => checkpoint.Name == checkpointName) > 0 ? Task.CompletedTask : throw new CheckpointNotFoundException(name, checkpointName);
        }
    }

    public class VirtualizationServiceTests : IAsyncLifetime
    {
        private readonly MemoryHypervisor hypervisor = new();
        private ChrysalisBinaryServer server = null!;
        private ChrysalisBinaryClient connection = null!;
        private IVirtualizationService service = null!;

        public async Task InitializeAsync()
        {
            var chrysalis = new ChrysalisServer().Expose<IVirtualizationService>(new VirtualizationService(hypervisor));
            var options = new ChrysalisBinaryServerOptions();
            options.Endpoints.Add((SocketAddress.Loopback(AddressFamily.IPv4, 0), null));
            server = new ChrysalisBinaryServer(chrysalis, options);
            server.Start();
            connection = await ChrysalisBinaryClient.ConnectAsync("127.0.0.1", server.Endpoints[0].Port);
            service = connection.CreateClient<IVirtualizationService>();
        }

        public async Task DisposeAsync()
        {
            await connection.DisposeAsync();
            await server.StopAsync();
        }

        private static VirtualMachineSpec Spec(string name) => new()
        {
            Name = name,
            ProcessorCount = 1,
            MemoryBytes = 64L * 1024 * 1024,
            Disks = [new VirtualDiskSpec { Path = OperatingSystem.IsWindows() ? @"C:\VMs\disk.vhdx" : "/var/vms/disk.qcow2", SizeBytes = 1024L * 1024 * 1024 }],
            Network = "default"
        };

        [Fact]
        public async Task ManagesMachinesRemotely()
        {
            var created = await service.CreateMachine(Spec("build-agent"), CancellationToken.None);
            await service.Start("build-agent", CancellationToken.None);
            var running = await service.GetMachine("build-agent", CancellationToken.None);
            await service.Pause("build-agent", CancellationToken.None);
            await service.Resume("build-agent", CancellationToken.None);
            await service.Shutdown("build-agent", CancellationToken.None);

            Assert.Equal(VirtualMachineState.Off, created.State);
            Assert.Equal(64L * 1024 * 1024, created.MemoryBytes);
            Assert.Equal(VirtualMachineState.Running, running.State);
            Assert.Equal(TimeSpan.FromMinutes(90.5), running.Uptime);   // a TimeSpan over Protocol Buffers (google.protobuf.Duration)
            Assert.Equal(VirtualMachineState.Off, Assert.Single(await service.ListMachines(CancellationToken.None)).State);
        }

        [Fact]
        public async Task ManagesCheckpoints()
        {
            await service.CreateMachine(Spec("db"), CancellationToken.None);

            var checkpoint = await service.CreateCheckpoint("db", "before upgrade", CancellationToken.None);
            await service.RestoreCheckpoint("db", "before upgrade", CancellationToken.None);
            var listed = await service.ListCheckpoints("db", CancellationToken.None);
            await service.DeleteCheckpoint("db", "before upgrade", CancellationToken.None);

            Assert.Equal("before upgrade", checkpoint.Name);
            Assert.Equal(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero), checkpoint.CreatedAt);
            Assert.Single(listed);
            Assert.Empty(await service.ListCheckpoints("db", CancellationToken.None));
        }

        [Fact]
        public async Task VirtualizationErrorsBecomeStatuses()
        {
            await service.CreateMachine(Spec("web"), CancellationToken.None);

            async Task<ChrysalisStatus> StatusOf(Func<Task> call) => (await Assert.ThrowsAsync<ChrysalisException>(call)).Status;

            Assert.Equal(ChrysalisStatus.NotFound, await StatusOf(() => service.GetMachine("ghost", CancellationToken.None)));
            Assert.Equal(ChrysalisStatus.AlreadyExists, await StatusOf(() => service.CreateMachine(Spec("web"), CancellationToken.None)));
            Assert.Equal(ChrysalisStatus.FailedPrecondition, await StatusOf(() => service.Pause("web", CancellationToken.None)));
            Assert.Equal(ChrysalisStatus.NotFound, await StatusOf(() => service.RestoreCheckpoint("web", "none", CancellationToken.None)));
            Assert.Equal(ChrysalisStatus.InvalidArgument, await StatusOf(() => service.CreateMachine(Spec("bad/name"), CancellationToken.None)));
            hypervisor.Available = false;
            Assert.Equal(ChrysalisStatus.Unavailable, await StatusOf(() => service.ListMachines(CancellationToken.None)));
            Assert.False(await service.IsAvailable(CancellationToken.None));
        }

        [Fact]
        public void ReportsTheReadinessOfTheHost()
        {
            var readiness = service.GetReadiness();

            Assert.Equal(VirtualizationHost.CheckReadiness().Platform, readiness.Platform);
            Assert.Equal(VirtualizationHost.CheckReadiness().IsReady, readiness.IsReady);
        }
    }
}

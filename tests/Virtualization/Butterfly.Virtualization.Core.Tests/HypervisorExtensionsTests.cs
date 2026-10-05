namespace Butterfly.Virtualization.Core.Tests
{
    public class HypervisorExtensionsTests
    {
        [Fact]
        public async Task GetThrowsWhenTheMachineDoesNotExist()
        {
            var exception = await Assert.ThrowsAsync<VirtualMachineNotFoundException>(() => new FakeHypervisor().GetAsync("missing"));

            Assert.Equal("missing", exception.Name);
        }

        [Fact]
        public async Task WaitsUntilTheMachineReachesTheState()
        {
            var hypervisor = new FakeHypervisor();
            hypervisor.Add("vm", VirtualMachineState.Starting);
            hypervisor.AfterFinds(2, "vm", VirtualMachineState.Running);

            var machine = await hypervisor.WaitForStateAsync("vm", VirtualMachineState.Running, TimeSpan.FromSeconds(10));

            Assert.Equal(VirtualMachineState.Running, machine.State);
        }

        [Fact]
        public async Task WaitingTimesOut()
        {
            var hypervisor = new FakeHypervisor();
            hypervisor.Add("vm", VirtualMachineState.Starting);

            await Assert.ThrowsAsync<TimeoutException>(() => hypervisor.WaitForStateAsync("vm", VirtualMachineState.Running, TimeSpan.FromMilliseconds(200)));
        }

        [Fact]
        public async Task StopShutsTheGuestDownWhenItCooperates()
        {
            var hypervisor = new FakeHypervisor { ShutdownWorks = true };
            hypervisor.Add("vm", VirtualMachineState.Running);

            await hypervisor.StopAsync("vm", TimeSpan.FromSeconds(10));

            Assert.Equal(["Shutdown"], hypervisor.Operations);
            Assert.Equal(VirtualMachineState.Off, (await hypervisor.GetAsync("vm")).State);
        }

        [Fact]
        public async Task StopTurnsTheMachineOffWhenTheGuestIgnoresTheShutdown()
        {
            var hypervisor = new FakeHypervisor { ShutdownWorks = false };
            hypervisor.Add("vm", VirtualMachineState.Running);

            await hypervisor.StopAsync("vm", TimeSpan.FromMilliseconds(300));

            Assert.Equal(["Shutdown", "TurnOff"], hypervisor.Operations);
            Assert.Equal(VirtualMachineState.Off, (await hypervisor.GetAsync("vm")).State);
        }

        [Fact]
        public async Task StopTurnsAPausedMachineOffDirectly()
        {
            var hypervisor = new FakeHypervisor();
            hypervisor.Add("vm", VirtualMachineState.Paused);

            await hypervisor.StopAsync("vm", TimeSpan.FromSeconds(10));

            Assert.Equal(["TurnOff"], hypervisor.Operations);
        }

        [Fact]
        public async Task StopDoesNothingWhenTheMachineIsOff()
        {
            var hypervisor = new FakeHypervisor();
            hypervisor.Add("vm", VirtualMachineState.Off);

            await hypervisor.StopAsync("vm", TimeSpan.FromSeconds(10));

            Assert.Empty(hypervisor.Operations);
        }

        // Only what the extensions use is implemented.
        private sealed class FakeHypervisor : IHypervisor
        {
            private readonly Dictionary<string, VirtualMachine> machines = [];
            private (int Finds, string Name, VirtualMachineState State)? pendingChange;

            public bool ShutdownWorks { get; init; }
            public List<string> Operations { get; } = [];
            public HypervisorPlatform Platform => HypervisorPlatform.Unknown;

            public void Add(string name, VirtualMachineState state) => machines[name] = new VirtualMachine { Id = name, Name = name, State = state };
            public void AfterFinds(int finds, string name, VirtualMachineState state) => pendingChange = (finds, name, state);

            public Task<VirtualMachine?> FindAsync(string name, CancellationToken cancellationToken = default)
            {
                if (pendingChange is { } change)
                {
                    if (change.Finds <= 0)
                    {
                        Add(change.Name, change.State);
                        pendingChange = null;
                    }
                    else
                    {
                        pendingChange = (change.Finds - 1, change.Name, change.State);
                    }
                }
                return Task.FromResult(machines.GetValueOrDefault(name));
            }

            public Task ShutdownAsync(string name, CancellationToken cancellationToken = default)
            {
                Operations.Add("Shutdown");
                if (ShutdownWorks)
                    Add(name, VirtualMachineState.Off);
                return Task.CompletedTask;
            }

            public Task TurnOffAsync(string name, CancellationToken cancellationToken = default)
            {
                Operations.Add("TurnOff");
                Add(name, VirtualMachineState.Off);
                return Task.CompletedTask;
            }

            public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
            public Task<IReadOnlyList<VirtualMachine>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<VirtualMachine>>([.. machines.Values]);
            public Task<VirtualMachine> CreateAsync(VirtualMachineSpec spec, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public Task DeleteAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public Task StartAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public Task PauseAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public Task ResumeAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public Task SaveAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public Task<IReadOnlyList<VirtualMachineCheckpoint>> ListCheckpointsAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public Task<VirtualMachineCheckpoint> CreateCheckpointAsync(string name, string checkpointName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public Task RestoreCheckpointAsync(string name, string checkpointName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public Task DeleteCheckpointAsync(string name, string checkpointName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        }
    }
}

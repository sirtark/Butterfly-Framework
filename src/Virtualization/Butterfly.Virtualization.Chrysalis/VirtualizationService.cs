using Butterfly.Chrysalis;

namespace Butterfly.Virtualization.Chrysalis
{
    /// <summary>Manages the virtual machines of one hypervisor remotely. Over REST: /virtualization/machines/{name}/...</summary>
    [ChrysalisService(Namespace = "butterfly.virtualization.v1", Route = "virtualization")]
    public interface IVirtualizationService
    {
        /// <summary>Whether this host can run virtual machines (it does not need the hypervisor to be installed).</summary>
        [HttpGet("readiness")]
        HostReadiness GetReadiness();

        [HttpGet("available")]
        Task<bool> IsAvailable(CancellationToken cancellationToken);

        [HttpGet("machines")]
        Task<IReadOnlyList<VirtualMachine>> ListMachines(CancellationToken cancellationToken);

        [HttpGet("machines/{name}")]
        Task<VirtualMachine> GetMachine(string name, CancellationToken cancellationToken);

        [HttpPost("machines")]
        Task<VirtualMachine> CreateMachine(VirtualMachineSpec spec, CancellationToken cancellationToken);

        [HttpDelete("machines/{name}")]
        Task DeleteMachine(string name, CancellationToken cancellationToken);

        [HttpPost("machines/{name}/start")]
        Task Start(string name, CancellationToken cancellationToken);

        [HttpPost("machines/{name}/shutdown")]
        Task Shutdown(string name, CancellationToken cancellationToken);

        [HttpPost("machines/{name}/turn-off")]
        Task TurnOff(string name, CancellationToken cancellationToken);

        [HttpPost("machines/{name}/pause")]
        Task Pause(string name, CancellationToken cancellationToken);

        [HttpPost("machines/{name}/resume")]
        Task Resume(string name, CancellationToken cancellationToken);

        [HttpPost("machines/{name}/save")]
        Task Save(string name, CancellationToken cancellationToken);

        [HttpGet("machines/{name}/checkpoints")]
        Task<IReadOnlyList<VirtualMachineCheckpoint>> ListCheckpoints(string name, CancellationToken cancellationToken);

        [HttpPost("machines/{name}/checkpoints/{checkpoint}")]
        Task<VirtualMachineCheckpoint> CreateCheckpoint(string name, string checkpoint, CancellationToken cancellationToken);

        [HttpPost("machines/{name}/checkpoints/{checkpoint}/restore")]
        Task RestoreCheckpoint(string name, string checkpoint, CancellationToken cancellationToken);

        [HttpDelete("machines/{name}/checkpoints/{checkpoint}")]
        Task DeleteCheckpoint(string name, string checkpoint, CancellationToken cancellationToken);
    }

    public sealed record ReadinessIssue(VirtualizationIssueKind Kind, bool IsBlocking, string Message);

    public sealed record HostReadiness(HypervisorPlatform Platform, bool IsReady, bool IsNested, IReadOnlyList<ReadinessIssue> Issues)
    {
        public static HostReadiness From(VirtualizationReadiness readiness) => new(readiness.Platform, readiness.IsReady, readiness.IsNested,
            [.. readiness.Issues.Select(issue => new ReadinessIssue(issue.Kind, issue.IsBlocking, issue.Message))]);
    }

    /// <summary>Exposes an <see cref="IHypervisor"/>; virtualization errors become the matching Chrysalis statuses.</summary>
    public sealed class VirtualizationService(IHypervisor hypervisor) : IVirtualizationService
    {
        public HostReadiness GetReadiness() => HostReadiness.From(VirtualizationHost.CheckReadiness());

        public Task<bool> IsAvailable(CancellationToken cancellationToken) => hypervisor.IsAvailableAsync(cancellationToken);

        public Task<IReadOnlyList<VirtualMachine>> ListMachines(CancellationToken cancellationToken) => Run(() => hypervisor.ListAsync(cancellationToken));

        public Task<VirtualMachine> GetMachine(string name, CancellationToken cancellationToken) => Run(() => hypervisor.GetAsync(name, cancellationToken));

        public Task<VirtualMachine> CreateMachine(VirtualMachineSpec spec, CancellationToken cancellationToken) => Run(() => hypervisor.CreateAsync(spec, cancellationToken));

        public Task DeleteMachine(string name, CancellationToken cancellationToken) => Run(() => hypervisor.DeleteAsync(name, cancellationToken));
        public Task Start(string name, CancellationToken cancellationToken) => Run(() => hypervisor.StartAsync(name, cancellationToken));
        public Task Shutdown(string name, CancellationToken cancellationToken) => Run(() => hypervisor.ShutdownAsync(name, cancellationToken));
        public Task TurnOff(string name, CancellationToken cancellationToken) => Run(() => hypervisor.TurnOffAsync(name, cancellationToken));
        public Task Pause(string name, CancellationToken cancellationToken) => Run(() => hypervisor.PauseAsync(name, cancellationToken));
        public Task Resume(string name, CancellationToken cancellationToken) => Run(() => hypervisor.ResumeAsync(name, cancellationToken));
        public Task Save(string name, CancellationToken cancellationToken) => Run(() => hypervisor.SaveAsync(name, cancellationToken));

        public Task<IReadOnlyList<VirtualMachineCheckpoint>> ListCheckpoints(string name, CancellationToken cancellationToken) =>
            Run(() => hypervisor.ListCheckpointsAsync(name, cancellationToken));

        public Task<VirtualMachineCheckpoint> CreateCheckpoint(string name, string checkpoint, CancellationToken cancellationToken) =>
            Run(() => hypervisor.CreateCheckpointAsync(name, checkpoint, cancellationToken));

        public Task RestoreCheckpoint(string name, string checkpoint, CancellationToken cancellationToken) =>
            Run(() => hypervisor.RestoreCheckpointAsync(name, checkpoint, cancellationToken));

        public Task DeleteCheckpoint(string name, string checkpoint, CancellationToken cancellationToken) =>
            Run(() => hypervisor.DeleteCheckpointAsync(name, checkpoint, cancellationToken));

        private static async Task<T> Run<T>(Func<Task<T>> operation)
        {
            try
            {
                return await operation().ConfigureAwait(false);
            }
            catch (VirtualizationException exception)
            {
                throw Translate(exception);
            }
        }

        private static async Task Run(Func<Task> operation)
        {
            try
            {
                await operation().ConfigureAwait(false);
            }
            catch (VirtualizationException exception)
            {
                throw Translate(exception);
            }
        }

        internal static ChrysalisException Translate(VirtualizationException exception) => new(exception switch
        {
            VirtualMachineNotFoundException or CheckpointNotFoundException => ChrysalisStatus.NotFound,
            VirtualMachineAlreadyExistsException => ChrysalisStatus.AlreadyExists,
            InvalidVirtualMachineStateException => ChrysalisStatus.FailedPrecondition,
            HypervisorUnavailableException => ChrysalisStatus.Unavailable,
            _ => ChrysalisStatus.Aborted
        }, exception.Message, exception);
    }
}

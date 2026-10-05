using Butterfly.Workflows.Runtime;

namespace Butterfly.Workflows
{
    public sealed record WorkflowError(string Message, string? ElementId = null, string? Stage = null, string? ComponentType = null);

    public enum WorkflowOperationStatus : byte
    {
        Succeeded,
        // A validation failed; the instance was left exactly as it was.
        Rejected,
        // Nothing in the instance reacted to the event.
        NotHandled,
        // A component threw; the instance was saved as faulted.
        Faulted
    }

    public sealed class WorkflowOperationResult(WorkflowOperationStatus status, WorkflowInstance? instance, IReadOnlyList<WorkflowError>? errors = null)
    {
        public WorkflowOperationStatus Status { get; } = status;

        // The saved instance, or its last persisted state when the operation was rejected or not handled. Null when a start is rejected.
        public WorkflowInstance? Instance { get; } = instance;
        public IReadOnlyList<WorkflowError> Errors { get; } = errors ?? [];

        public bool Succeeded => Status == WorkflowOperationStatus.Succeeded;
    }
}

namespace Butterfly.Workflows.Runtime
{
    public sealed class WorkflowHistoryEntry
    {
        public DateTimeOffset Timestamp { get; set; }
        public WorkflowHistoryKind Kind { get; set; }
        public string? ElementId { get; set; }
        public string? TokenId { get; set; }
        public string? Message { get; set; }
    }

    public enum WorkflowHistoryKind : byte
    {
        WorkflowStarted,
        WorkflowCompleted,
        WorkflowFaulted,
        WorkflowCancelled,
        EventReceived,
        BranchEntered,
        BranchExited,
        NodeEntered,
        NodeWaiting,
        NodeExited,
        TransitionTaken,
        Jumped,
        TokenCancelled,
        StepFailed
    }
}

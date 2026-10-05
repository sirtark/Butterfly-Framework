namespace Butterfly.Workflows.Runtime
{
    public sealed class WorkflowFault
    {
        public string? ElementId { get; set; }
        public string? Stage { get; set; }
        public string? ComponentType { get; set; }
        public string Message { get; set; } = string.Empty;
        public string? ExceptionType { get; set; }
        public string? StackTrace { get; set; }
        public DateTimeOffset Timestamp { get; set; }
    }
}

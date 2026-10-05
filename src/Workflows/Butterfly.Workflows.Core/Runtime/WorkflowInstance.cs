using System.Text.Json.Nodes;

namespace Butterfly.Workflows.Runtime
{
    public sealed class WorkflowInstance
    {
        public string Id { get; set; } = string.Empty;
        public string DefinitionId { get; set; } = string.Empty;
        public int DefinitionVersion { get; set; }
        public WorkflowStatus Status { get; set; }
        public string? CorrelationId { get; set; }

        public JsonNode? Input { get; set; }
        public JsonObject Variables { get; set; } = new();

        // Outputs of nodes and branches, keyed by element id.
        public JsonObject Outputs { get; set; } = new();
        public JsonNode? Output { get; set; }

        public List<WorkflowToken> Tokens { get; set; } = [];
        public List<WorkflowHistoryEntry> History { get; set; } = [];
        public WorkflowFault? Fault { get; set; }
        public JsonObject? Metadata { get; set; }

        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }

        // Incremented by the instance store on every save, for optimistic concurrency.
        public long Revision { get; set; }
    }

    public enum WorkflowStatus : byte
    {
        Running,
        Completed,
        Faulted,
        Cancelled
    }
}

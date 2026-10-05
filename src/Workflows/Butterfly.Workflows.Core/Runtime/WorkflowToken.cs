using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Butterfly.Workflows.Runtime
{
    // One thread of execution. Child branches get their own tokens, whose parent waits at the node that owns them.
    public sealed class WorkflowToken
    {
        public string Id { get; set; } = string.Empty;
        public string? ParentId { get; set; }
        public string BranchId { get; set; } = string.Empty;
        public string? NodeId { get; set; }
        public WorkflowTokenStatus Status { get; set; }

        // Child branches still to run when the owning node runs them sequentially.
        public List<string> PendingBranchIds { get; set; } = [];

        // Free storage for node types.
        public JsonObject? State { get; set; }

        public DateTimeOffset CreatedAt { get; set; }

        [JsonIgnore]
        public string PositionId => NodeId ?? BranchId;
    }

    public enum WorkflowTokenStatus : byte
    {
        Running,
        Waiting,
        WaitingForChildren
    }
}

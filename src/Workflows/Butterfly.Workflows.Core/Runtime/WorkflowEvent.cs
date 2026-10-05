using System.Text.Json.Nodes;

namespace Butterfly.Workflows.Runtime
{
    public sealed class WorkflowEvent
    {
        public WorkflowEvent(string name, JsonNode? payload = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name, nameof(name));
            Name = name;
            Payload = payload;
        }

        public string Name { get; }
        public JsonNode? Payload { get; init; }

        // Restricts delivery to one token, or to the tokens inside a node or branch.
        public string? TargetId { get; init; }
        public string? CorrelationId { get; init; }
    }
}

using System.Text.Json.Nodes;

namespace Butterfly.Workflows.Definitions
{
    public abstract class WorkflowElementDefinition
    {
        public string Id { get; set; } = string.Empty;
        public string? Name { get; set; }
        public string? Description { get; set; }
        public JsonObject? Metadata { get; set; }
    }
}

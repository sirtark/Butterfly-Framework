using System.Text.Json.Nodes;

namespace Butterfly.Workflows.Definitions
{
    public sealed class WorkflowDefinition : FlowElementDefinition, IBranchContainer
    {
        // How the workflow itself is referenced among its elements (tokens, history, graphs), so its id never clashes with a node or branch id.
        public const string RootId = "$workflow";

        public int Version { get; set; } = 1;
        public string? StartId { get; set; }
        public JsonObject? Variables { get; set; }
        public WorkflowDefinitionOptions Options { get; set; } = new();
        public List<EventDefinition> Events { get; set; } = [];
        public List<BranchDefinition> Branches { get; set; } = [];
        public List<TransitionDefinition> Transitions { get; set; } = [];
    }
}

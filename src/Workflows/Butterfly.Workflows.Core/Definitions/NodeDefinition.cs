using System.Text.Json.Nodes;

namespace Butterfly.Workflows.Definitions
{
    public sealed class NodeDefinition : FlowElementDefinition, IBranchContainer
    {
        // A node type registered in the component catalog; "task" when empty.
        public string? Type { get; set; }
        public JsonObject? Settings { get; set; }

        // Child branches start when the node is entered, and the node runs once they join.
        public List<BranchDefinition> Branches { get; set; } = [];
        public ChildBranchMode ChildBranchMode { get; set; } = ChildBranchMode.Parallel;
        public JoinMode JoinMode { get; set; } = JoinMode.All;
    }
}

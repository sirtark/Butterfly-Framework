namespace Butterfly.Workflows.Definitions
{
    public sealed class BranchDefinition : FlowElementDefinition, IBranchContainer
    {
        public string? StartNodeId { get; set; }

        // Whether transitions and jumps from outside the branch may enter it or leave it.
        public bool AllowExternalEntry { get; set; } = true;
        public bool AllowExternalExit { get; set; } = true;

        public List<NodeDefinition> Nodes { get; set; } = [];

        // Nested branches, entered through transitions.
        public List<BranchDefinition> Branches { get; set; } = [];
    }
}

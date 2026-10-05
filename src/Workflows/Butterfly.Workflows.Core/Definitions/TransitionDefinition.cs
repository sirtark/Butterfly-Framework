namespace Butterfly.Workflows.Definitions
{
    // Moves a token from a node or branch to any other node or branch of the workflow, in any branch.
    public sealed class TransitionDefinition : WorkflowElementDefinition
    {
        public string SourceId { get; set; } = string.Empty;
        public string TargetId { get; set; } = string.Empty;
        public TransitionTrigger Trigger { get; set; } = TransitionTrigger.Automatic;
        public string? Event { get; set; }
        public int Priority { get; set; }
        public List<ConditionDefinition> Conditions { get; set; } = [];
        public PipelineDefinition? Pipeline { get; set; }
    }
}

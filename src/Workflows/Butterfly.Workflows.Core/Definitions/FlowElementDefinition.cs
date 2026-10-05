namespace Butterfly.Workflows.Definitions
{
    // The workflow, its branches and its nodes: elements a token can be inside of, each with its own pipelines and output.
    public abstract class FlowElementDefinition : WorkflowElementDefinition
    {
        public Dictionary<string, PipelineDefinition> Pipelines { get; set; } = [];
        public OutputDefinition? Output { get; set; }
        public ElementLayout? Layout { get; set; }

        public PipelineDefinition? GetPipeline(string stage)
            => Pipelines.GetValueOrDefault(stage);
    }
}

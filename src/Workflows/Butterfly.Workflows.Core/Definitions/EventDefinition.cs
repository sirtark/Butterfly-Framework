using System.Text.Json.Nodes;

namespace Butterfly.Workflows.Definitions
{
    // An input event the workflow accepts. The id is the event name used by transitions.
    public sealed class EventDefinition : WorkflowElementDefinition
    {
        // Publishing this event through IWorkflowEngine.DispatchAsync starts a new instance.
        public bool StartsWorkflow { get; set; }
        public PipelineDefinition? Pipeline { get; set; }
        public JsonObject? PayloadSchema { get; set; }
    }
}

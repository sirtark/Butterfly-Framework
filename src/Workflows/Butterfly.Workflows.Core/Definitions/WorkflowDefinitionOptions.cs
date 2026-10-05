namespace Butterfly.Workflows.Definitions
{
    public sealed class WorkflowDefinitionOptions
    {
        // Rejects events that are not declared in WorkflowDefinition.Events.
        public bool StrictEvents { get; set; }

        // Allows moving a token to any node or branch at runtime, without a declared transition.
        public bool AllowJumps { get; set; }
    }
}

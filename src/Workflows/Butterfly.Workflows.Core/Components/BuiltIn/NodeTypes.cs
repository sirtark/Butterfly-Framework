namespace Butterfly.Workflows.Components.BuiltIn
{
    public static class WorkflowNodeTypes
    {
        public const string Task = "task";
        public const string Wait = "wait";
        public const string End = "end";
    }

    [WorkflowComponent(WorkflowNodeTypes.Task, DisplayName = "Task", Category = "Flow", Description = "Runs its pipelines and continues right away.")]
    public sealed class TaskNodeType : IWorkflowNodeType
    {
        public ValueTask<NodeResult> ExecuteAsync(WorkflowContext context) => ValueTask.FromResult(NodeResult.Complete);
    }

    public sealed class WaitNodeSettings
    {
        // Completes the node when this event arrives and no transition consumed it; the payload becomes the node output.
        public string? Event { get; set; }
    }

    [WorkflowComponent(WorkflowNodeTypes.Wait, DisplayName = "Wait", Category = "Flow", SettingsType = typeof(WaitNodeSettings),
        Description = "Stops until a transition moves the token or the configured event arrives.")]
    public sealed class WaitNodeType : IWorkflowNodeType
    {
        public ValueTask<NodeResult> ExecuteAsync(WorkflowContext context) => ValueTask.FromResult(NodeResult.Wait);

        public ValueTask<NodeResult> HandleEventAsync(WorkflowContext context)
        {
            var settings = context.GetSettings<WaitNodeSettings>();
            if (settings.Event is null || context.Event is not { } received || received.Name != settings.Event)
                return ValueTask.FromResult(NodeResult.Ignore);

            context.SetOutput(received.Payload);
            return ValueTask.FromResult(NodeResult.Complete);
        }
    }

    public enum EndScope : byte
    {
        Branch,
        Workflow
    }

    public sealed class EndNodeSettings
    {
        public EndScope Scope { get; set; } = EndScope.Branch;
    }

    [WorkflowComponent(WorkflowNodeTypes.End, DisplayName = "End", Category = "Flow", SettingsType = typeof(EndNodeSettings),
        Description = "Ends its branch, or the whole workflow.")]
    public sealed class EndNodeType : IWorkflowNodeType
    {
        public ValueTask<NodeResult> ExecuteAsync(WorkflowContext context)
            => ValueTask.FromResult(context.GetSettings<EndNodeSettings>().Scope == EndScope.Workflow ? NodeResult.EndWorkflow : NodeResult.EndBranch);
    }
}

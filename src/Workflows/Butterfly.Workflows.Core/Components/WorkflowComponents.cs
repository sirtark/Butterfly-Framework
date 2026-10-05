using System.Text.Json.Nodes;

namespace Butterfly.Workflows.Components
{
    public interface IWorkflowNodeType
    {
        ValueTask<NodeResult> ExecuteAsync(WorkflowContext context);

        // Called for events that no transition consumed while the token waits at the node.
        ValueTask<NodeResult> HandleEventAsync(WorkflowContext context) => ValueTask.FromResult(NodeResult.Ignore);
    }

    public interface IWorkflowStep
    {
        ValueTask ExecuteAsync(WorkflowContext context);
    }

    public interface IWorkflowValidator
    {
        // Returns the validation errors; an empty list means valid.
        ValueTask<IReadOnlyList<string>> ValidateAsync(WorkflowContext context);
    }

    public interface IWorkflowCondition
    {
        ValueTask<bool> EvaluateAsync(WorkflowContext context);
    }

    public interface IWorkflowOutput
    {
        ValueTask<JsonNode?> MapAsync(WorkflowContext context);
    }

    public enum WorkflowComponentKind : byte
    {
        NodeType,
        Step,
        Validator,
        Condition,
        Output
    }

    public enum NodeResultKind : byte
    {
        Complete,
        Wait,
        EndBranch,
        EndWorkflow,
        Ignore
    }

    public readonly record struct NodeResult(NodeResultKind Kind)
    {
        // The node finished; its automatic transitions are evaluated.
        public static NodeResult Complete => new(NodeResultKind.Complete);
        // The token stays at the node until an event or transition moves it.
        public static NodeResult Wait => new(NodeResultKind.Wait);
        // The node finished and its branch ends, ignoring the node's own transitions.
        public static NodeResult EndBranch => new(NodeResultKind.EndBranch);
        // Every token is stopped and the workflow completes.
        public static NodeResult EndWorkflow => new(NodeResultKind.EndWorkflow);
        // Only for HandleEventAsync: the node does not care about the event.
        public static NodeResult Ignore => new(NodeResultKind.Ignore);
    }
}

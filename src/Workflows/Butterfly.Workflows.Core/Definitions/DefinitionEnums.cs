namespace Butterfly.Workflows.Definitions
{
    public enum TransitionTrigger : byte
    {
        // Taken when the source finishes and its conditions pass.
        Automatic,
        // Taken when the named event is published.
        Event,
        // Taken only through IWorkflowEngine.TriggerTransitionAsync.
        Manual
    }

    public enum ChildBranchMode : byte
    {
        Parallel,
        Sequential
    }

    public enum JoinMode : byte
    {
        // The node runs when every child branch finished.
        All,
        // The node runs when the first child branch finishes; the others are cancelled.
        Any,
        // The node runs right away; child branches keep running on their own.
        None
    }

    public enum StepErrorBehavior : byte
    {
        Fail,
        Continue
    }
}

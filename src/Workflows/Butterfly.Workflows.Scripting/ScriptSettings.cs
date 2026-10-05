using Butterfly.Scripting;

namespace Butterfly.Workflows.Scripting
{
    public class ScriptSettings
    {
        // Falls back to WorkflowScriptingOptions.DefaultLanguage.
        public ScriptingLanguage? Language { get; set; }
        public string Source { get; set; } = string.Empty;

        // A keyed IScriptInvoker; falls back to WorkflowScriptingOptions.InvokerKey, then to the unkeyed invoker.
        public string? InvokerKey { get; set; }

        // Extra script parameters read from workflow paths, e.g. { "amount": "variables.order.amount" }.
        public Dictionary<string, string>? Parameters { get; set; }
    }

    public sealed class ScriptStepSettings : ScriptSettings
    {
        // Stores the script result in this variable.
        public string? AssignTo { get; set; }

        // Copies every property of the returned object into the workflow variables.
        public bool Merge { get; set; }
    }

    public sealed class ScriptNodeSettings : ScriptSettings
    {
        // Runs when an event reaches the waiting node; returning nothing ignores the event.
        public string? EventSource { get; set; }
    }

    public sealed class WorkflowScriptingOptions
    {
        public ScriptingLanguage? DefaultLanguage { get; set; }
        public string? InvokerKey { get; set; }
    }
}

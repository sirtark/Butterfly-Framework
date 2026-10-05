using System.Text.Json.Nodes;

namespace Butterfly.Workflows.Definitions
{
    // A reference to a component registered in the catalog, plus the settings it runs with.
    public abstract class ComponentDefinition
    {
        public string Type { get; set; } = string.Empty;
        public JsonObject? Settings { get; set; }
    }

    public sealed class StepDefinition : ComponentDefinition
    {
        public string? Name { get; set; }
        public List<ConditionDefinition> Conditions { get; set; } = [];
        public StepErrorBehavior OnError { get; set; } = StepErrorBehavior.Fail;
    }

    public sealed class ValidationDefinition : ComponentDefinition
    {
        // Replaces the messages reported by the validator.
        public string? Message { get; set; }
    }

    public sealed class ConditionDefinition : ComponentDefinition
    {
        public bool Negate { get; set; }
    }

    public sealed class OutputDefinition : ComponentDefinition;
}

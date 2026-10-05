using System.Text.Json.Nodes;

namespace Butterfly.Workflows.Components.BuiltIn
{
    public sealed class RequiredSettings
    {
        public List<string> Paths { get; set; } = [];
    }

    [WorkflowComponent("required", DisplayName = "Required", Category = "Validation", SettingsType = typeof(RequiredSettings),
        Description = "Fails when any of the paths is missing, null or empty.")]
    public sealed class RequiredValidator : IWorkflowValidator
    {
        public ValueTask<IReadOnlyList<string>> ValidateAsync(WorkflowContext context)
        {
            IReadOnlyList<string> errors = [.. context.GetSettings<RequiredSettings>().Paths
                .Where(path => JsonValues.IsEmpty(context.GetValue(path)))
                .Select(path => $"'{path}' is required.")];
            return ValueTask.FromResult(errors);
        }
    }

    public enum CompareOperator : byte
    {
        Equal,
        NotEqual,
        GreaterThan,
        GreaterThanOrEqual,
        LessThan,
        LessThanOrEqual,
        Contains,
        Exists,
        NotExists
    }

    public sealed class CompareSettings
    {
        public string Path { get; set; } = string.Empty;
        public CompareOperator Operator { get; set; } = CompareOperator.Equal;

        // The value to compare with, or a path to read it from.
        public JsonNode? Value { get; set; }
        public string? ValuePath { get; set; }
    }

    [WorkflowComponent("compare", DisplayName = "Compare", Category = "Logic", SettingsType = typeof(CompareSettings),
        Description = "Compares the value at a path with a literal or another path.")]
    public sealed class CompareComponent : IWorkflowCondition, IWorkflowValidator
    {
        public ValueTask<bool> EvaluateAsync(WorkflowContext context)
            => ValueTask.FromResult(Evaluate(context, context.GetSettings<CompareSettings>()));

        public ValueTask<IReadOnlyList<string>> ValidateAsync(WorkflowContext context)
        {
            var settings = context.GetSettings<CompareSettings>();
            IReadOnlyList<string> errors = Evaluate(context, settings)
                ? []
                : [$"'{settings.Path}' must be {settings.Operator} {JsonValues.ToText(Expected(context, settings))}."];
            return ValueTask.FromResult(errors);
        }

        static bool Evaluate(WorkflowContext context, CompareSettings settings)
        {
            var actual = context.GetValue(settings.Path);
            var expected = Expected(context, settings);
            return settings.Operator switch
            {
                CompareOperator.Exists => actual is not null,
                CompareOperator.NotExists => actual is null,
                CompareOperator.Equal => JsonValues.AreEqual(actual, expected),
                CompareOperator.NotEqual => !JsonValues.AreEqual(actual, expected),
                CompareOperator.GreaterThan => JsonValues.Compare(actual, expected) > 0,
                CompareOperator.GreaterThanOrEqual => JsonValues.Compare(actual, expected) >= 0,
                CompareOperator.LessThan => JsonValues.Compare(actual, expected) < 0,
                CompareOperator.LessThanOrEqual => JsonValues.Compare(actual, expected) <= 0,
                CompareOperator.Contains => JsonValues.Contains(actual, expected),
                _ => false
            };
        }

        static JsonNode? Expected(WorkflowContext context, CompareSettings settings)
            => settings.ValuePath is null ? settings.Value : context.GetValue(settings.ValuePath);
    }

    public sealed class VariablesOutputSettings
    {
        // Every variable when empty.
        public List<string> Names { get; set; } = [];
    }

    [WorkflowComponent("variables", DisplayName = "Variables", Category = "Data", SettingsType = typeof(VariablesOutputSettings),
        Description = "Outputs an object with the selected workflow variables.")]
    public sealed class VariablesOutput : IWorkflowOutput
    {
        public ValueTask<JsonNode?> MapAsync(WorkflowContext context)
        {
            var names = context.GetSettings<VariablesOutputSettings>().Names;
            var output = new JsonObject();
            foreach (var (name, value) in context.Variables)
            {
                if (names.Count == 0 || names.Contains(name))
                    output[name] = value?.DeepClone();
            }
            return ValueTask.FromResult<JsonNode?>(output);
        }
    }

    public sealed class ValueOutputSettings
    {
        public JsonNode? Value { get; set; }
        public string? Path { get; set; }
    }

    [WorkflowComponent("value", DisplayName = "Value", Category = "Data", SettingsType = typeof(ValueOutputSettings),
        Description = "Outputs a literal value, or the value at a path.")]
    public sealed class ValueOutput : IWorkflowOutput
    {
        public ValueTask<JsonNode?> MapAsync(WorkflowContext context)
        {
            var settings = context.GetSettings<ValueOutputSettings>();
            return ValueTask.FromResult(settings.Path is null ? settings.Value?.DeepClone() : context.GetValue(settings.Path)?.DeepClone());
        }
    }
}

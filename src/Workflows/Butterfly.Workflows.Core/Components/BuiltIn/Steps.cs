using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Butterfly.Workflows.Components.BuiltIn
{
    public sealed class SetVariablesSettings
    {
        // Literal values to assign.
        public JsonObject? Values { get; set; }

        // Variables copied from paths, e.g. { "comment": "event.comment" }.
        public Dictionary<string, string>? From { get; set; }
    }

    [WorkflowComponent("set-variables", DisplayName = "Set variables", Category = "Data", SettingsType = typeof(SetVariablesSettings),
        Description = "Assigns literal values or values read from paths to workflow variables.")]
    public sealed class SetVariablesStep : IWorkflowStep
    {
        public ValueTask ExecuteAsync(WorkflowContext context)
        {
            var settings = context.GetSettings<SetVariablesSettings>();
            foreach (var (name, value) in settings.Values ?? new JsonObject())
                context.Variables[name] = value?.DeepClone();
            foreach (var (name, path) in settings.From ?? new Dictionary<string, string>())
                context.Variables[name] = context.GetValue(path)?.DeepClone();
            return ValueTask.CompletedTask;
        }
    }

    public sealed class LogSettings
    {
        // Placeholders such as {variables.amount} are replaced with values.
        public string Message { get; set; } = string.Empty;
        public LogLevel Level { get; set; } = LogLevel.Information;
    }

    [WorkflowComponent("log", DisplayName = "Log", Category = "Diagnostics", SettingsType = typeof(LogSettings),
        Description = "Writes a message to the Butterfly.Workflows logger.")]
    public sealed class LogStep : IWorkflowStep
    {
        public ValueTask ExecuteAsync(WorkflowContext context)
        {
            var settings = context.GetSettings<LogSettings>();
            var logger = context.Services.GetService<ILoggerFactory>()?.CreateLogger("Butterfly.Workflows");
            if (logger?.IsEnabled(settings.Level) == true)
                logger.Log(settings.Level, "[{WorkflowId}/{InstanceId}/{ElementId}] {Message}", context.Definition.Id, context.Instance.Id, context.Element.Id, context.Render(settings.Message));
            return ValueTask.CompletedTask;
        }
    }

    public sealed class FailSettings
    {
        public string? Message { get; set; }
    }

    [WorkflowComponent("fail", DisplayName = "Fail", Category = "Diagnostics", SettingsType = typeof(FailSettings),
        Description = "Throws, faulting the instance unless the step continues on error.")]
    public sealed class FailStep : IWorkflowStep
    {
        public ValueTask ExecuteAsync(WorkflowContext context)
            => throw new WorkflowException(context.Render(context.GetSettings<FailSettings>().Message ?? $"The step at '{context.Element.Id}' failed on purpose."));
    }
}

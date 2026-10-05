using System.Collections;
using System.Text.Json.Nodes;
using Butterfly.Workflows.Components;

namespace Butterfly.Workflows.Scripting
{
    public static class ScriptComponentTypes
    {
        public const string Script = "script";
    }

    [WorkflowComponent(ScriptComponentTypes.Script, DisplayName = "Script", Category = "Scripting", SettingsType = typeof(ScriptStepSettings),
        Description = "Runs a script; its result can be stored in a variable or merged into the variables.")]
    public sealed class ScriptStep(WorkflowScriptRunner runner) : IWorkflowStep
    {
        public async ValueTask ExecuteAsync(WorkflowContext context)
        {
            var settings = context.GetSettings<ScriptStepSettings>();
            var result = await runner.RunAsync(context, settings);

            if (settings.AssignTo is not null)
                context.Variables[settings.AssignTo] = WorkflowScriptRunner.ToJson(result);
            if (settings.Merge && result is not null)
            {
                if (WorkflowScriptRunner.ToJson(result) is not JsonObject values)
                    throw new WorkflowException($"The script at '{context.Element.Id}' must return an object to merge it into the variables.");
                foreach (var (name, value) in values.ToList())
                {
                    values.Remove(name);
                    context.Variables[name] = value;
                }
            }
        }
    }

    [WorkflowComponent(ScriptComponentTypes.Script, DisplayName = "Script", Category = "Scripting", SettingsType = typeof(ScriptSettings),
        Description = "Passes when the script returns true.")]
    public sealed class ScriptCondition(WorkflowScriptRunner runner) : IWorkflowCondition
    {
        public async ValueTask<bool> EvaluateAsync(WorkflowContext context)
            => await runner.RunAsync(context, context.GetSettings<ScriptSettings>()) switch
            {
                bool passed => passed,
                null => false,
                var other => throw new WorkflowException($"The condition script at '{context.Element.Id}' must return a boolean, but returned {other.GetType().Name}.")
            };
    }

    [WorkflowComponent(ScriptComponentTypes.Script, DisplayName = "Script", Category = "Scripting", SettingsType = typeof(ScriptSettings),
        Description = "Valid when the script returns nothing or true; false, a message or a list of messages are errors.")]
    public sealed class ScriptValidator(WorkflowScriptRunner runner) : IWorkflowValidator
    {
        public async ValueTask<IReadOnlyList<string>> ValidateAsync(WorkflowContext context)
            => await runner.RunAsync(context, context.GetSettings<ScriptSettings>()) switch
            {
                null or true => [],
                false => ["The validation script failed."],
                string message => message.Length == 0 ? [] : [message],
                IDictionary { Count: 0 } => [],
                IEnumerable messages and not IDictionary => [.. messages.Cast<object?>().Select(message => message?.ToString() ?? string.Empty).Where(message => message.Length > 0)],
                var other => throw new WorkflowException($"The validation script at '{context.Element.Id}' returned {other.GetType().Name}; return nothing, a boolean, a message or a list of messages.")
            };
    }

    [WorkflowComponent(ScriptComponentTypes.Script, DisplayName = "Script", Category = "Scripting", SettingsType = typeof(ScriptSettings),
        Description = "Uses the script result as the output.")]
    public sealed class ScriptOutput(WorkflowScriptRunner runner) : IWorkflowOutput
    {
        public async ValueTask<JsonNode?> MapAsync(WorkflowContext context)
            => WorkflowScriptRunner.ToJson(await runner.RunAsync(context, context.GetSettings<ScriptSettings>()));
    }

    // The script decides what the node does: return "wait", "endBranch" or "endWorkflow" (or nothing to complete),
    // any other value to complete with it as the output, or { result = ..., output = ... } for both.
    [WorkflowComponent(ScriptComponentTypes.Script, DisplayName = "Script", Category = "Scripting", SettingsType = typeof(ScriptNodeSettings),
        Description = "A node whose behavior is written as a script, with an optional script for incoming events.")]
    public sealed class ScriptNodeType(WorkflowScriptRunner runner) : IWorkflowNodeType
    {
        public async ValueTask<NodeResult> ExecuteAsync(WorkflowContext context)
        {
            var settings = context.GetSettings<ScriptNodeSettings>();
            return Interpret(context, await runner.RunAsync(context, settings), NodeResult.Complete);
        }

        public async ValueTask<NodeResult> HandleEventAsync(WorkflowContext context)
        {
            var settings = context.GetSettings<ScriptNodeSettings>();
            if (string.IsNullOrWhiteSpace(settings.EventSource))
                return NodeResult.Ignore;
            return Interpret(context, await runner.RunAsync(context, settings, settings.EventSource), NodeResult.Ignore);
        }

        static NodeResult Interpret(WorkflowContext context, object? result, NodeResult whenEmpty)
        {
            switch (result)
            {
                case null:
                    return whenEmpty;
                case string text when Enum.TryParse<NodeResultKind>(text, ignoreCase: true, out var kind):
                    return new NodeResult(kind);
                case IDictionary dictionary when dictionary.Contains("result"):
                    if (dictionary.Contains("output"))
                        context.SetOutput(WorkflowScriptRunner.ToJson(dictionary["output"]));
                    return dictionary["result"] is string name && Enum.TryParse<NodeResultKind>(name, ignoreCase: true, out var named)
                        ? new NodeResult(named)
                        : throw new WorkflowException($"The script at '{context.Element.Id}' returned an unknown result '{dictionary["result"]}'.");
                default:
                    context.SetOutput(WorkflowScriptRunner.ToJson(result));
                    return NodeResult.Complete;
            }
        }
    }
}

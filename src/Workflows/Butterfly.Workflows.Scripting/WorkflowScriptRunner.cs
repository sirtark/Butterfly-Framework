using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Butterfly.Scripting;
using Butterfly.Workflows.Definitions;
using Butterfly.Workflows.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace Butterfly.Workflows.Scripting
{
    // Runs a script with the workflow data as parameters. Every script component goes through here, and custom components can too.
    public sealed class WorkflowScriptRunner(WorkflowScriptingOptions options)
    {
        public Task<object?> RunAsync(WorkflowContext context, ScriptSettings settings, string? source = null)
        {
            ArgumentNullException.ThrowIfNull(context, nameof(context));
            ArgumentNullException.ThrowIfNull(settings, nameof(settings));

            source ??= settings.Source;
            if (string.IsNullOrWhiteSpace(source))
                throw new WorkflowException($"The script at '{context.Element.Id}' has no source.");
            var language = settings.Language ?? options.DefaultLanguage
                ?? throw new WorkflowException($"The script at '{context.Element.Id}' does not say its language and no default language is configured.");

            var invokerKey = settings.InvokerKey ?? options.InvokerKey;
            var invoker = invokerKey is null
                ? context.Services.GetRequiredService<IScriptInvoker>()
                : context.Services.GetRequiredKeyedService<IScriptInvoker>(invokerKey);

            return invoker.InvokeScriptAsync(language, source, CreateParameters(context, settings.Parameters), context.Services, context.CancellationToken);
        }

        // variables, input, event (the payload), eventName, outputs and workflow (ids and position), plus the configured extra parameters.
        public static IReadOnlyDictionary<string, object> CreateParameters(WorkflowContext context, IReadOnlyDictionary<string, string>? extra = null)
        {
            ArgumentNullException.ThrowIfNull(context, nameof(context));

            // Scripting accepts null parameter values even though its dictionary is typed as non-nullable.
            var parameters = new Dictionary<string, object>
            {
                ["variables"] = ToClr(context.Variables)!,
                ["input"] = ToClr(context.Instance.Input)!,
                ["event"] = ToClr(context.Event?.Payload)!,
                ["eventName"] = context.Event?.Name!,
                ["outputs"] = ToClr(context.Instance.Outputs)!,
                ["workflow"] = new Dictionary<string, object?>
                {
                    ["id"] = context.Definition.Id,
                    ["version"] = (long)context.Definition.Version,
                    ["instanceId"] = context.Instance.Id,
                    ["correlationId"] = context.Instance.CorrelationId,
                    ["elementId"] = context.Element is WorkflowDefinition ? WorkflowDefinition.RootId : context.Element.Id,
                    ["tokenId"] = context.Token?.Id,
                    ["stage"] = context.Stage
                }
            };
            foreach (var (name, path) in extra ?? new Dictionary<string, string>())
                parameters[name] = ToClr(context.GetValue(path))!;
            return parameters;
        }

        // Plain CLR values understood by every script engine: dictionaries, lists, long, double, string, bool and null.
        public static object? ToClr(JsonNode? node)
            => node switch
            {
                null => null,
                JsonObject json => json.ToDictionary(property => property.Key, property => ToClr(property.Value)),
                JsonArray array => array.Select(ToClr).ToList(),
                JsonValue value => value.GetValueKind() switch
                {
                    JsonValueKind.String => value.GetValue<string>(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Number => long.TryParse(value.ToJsonString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)
                        ? (object)integer
                        : double.Parse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture),
                    _ => null
                },
                _ => null
            };

        public static JsonNode? ToJson(object? value)
            => value is null ? null : JsonSerializer.SerializeToNode(value, WorkflowJson.Options);
    }
}

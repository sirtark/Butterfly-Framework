using System.Text.Json;
using System.Text.Json.Nodes;
using Butterfly.Workflows.Definitions;
using Butterfly.Workflows.Runtime;
using Butterfly.Workflows.Serialization;

namespace Butterfly.Workflows
{
    // What a component sees while it runs: the element it is attached to, the instance data and the operation in progress.
    public sealed class WorkflowContext
    {
        readonly WorkflowOperation _operation;

        internal WorkflowContext(WorkflowOperation operation, WorkflowElementDefinition element, WorkflowToken? token, string? stage, ComponentDefinition? component)
        {
            _operation = operation;
            Element = element;
            Token = token;
            Stage = stage;
            Component = component;
        }

        public WorkflowDefinition Definition => _operation.Index.Definition;
        public WorkflowInstance Instance => _operation.Instance;
        public WorkflowElementDefinition Element { get; }
        public WorkflowToken? Token { get; }
        public string? Stage { get; }
        public ComponentDefinition? Component { get; }
        public WorkflowEvent? Event => _operation.Event;
        public IServiceProvider Services => _operation.Services;
        public CancellationToken CancellationToken => _operation.CancellationToken;

        public JsonObject Variables => Instance.Variables;

        // The component settings, or the node settings when a node type runs.
        public JsonObject? Settings => Component?.Settings ?? (Element as NodeDefinition)?.Settings;

        public T GetSettings<T>() where T : class, new()
            => Settings?.Deserialize<T>(WorkflowJson.Options) ?? new T();

        // Reads a value by path: variables.x, input.x, event.x (the payload), outputs.elementId.x or settings.x.
        public JsonNode? GetValue(string path)
            => WorkflowValuePath.Resolve(this, path);

        public JsonNode? GetOutput(string elementId)
            => Instance.Outputs[elementId];

        public void SetOutput(JsonNode? value)
            => _operation.SetOutput(Element, value);

        public string Render(string template)
            => WorkflowTemplate.Render(this, template);
    }
}

using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using Butterfly.Workflows.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace Butterfly.Workflows.Components
{
    // Describes a registered component: how to create it, and what an editor needs to show and configure it.
    public sealed class WorkflowComponentDescriptor
    {
        ObjectFactory? _factory;

        public WorkflowComponentDescriptor(WorkflowComponentKind kind, string type, Type implementationType)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(type, nameof(type));
            ArgumentNullException.ThrowIfNull(implementationType, nameof(implementationType));
            if (!ContractOf(kind).IsAssignableFrom(implementationType))
                throw new ArgumentException($"{implementationType} does not implement {ContractOf(kind).Name}.", nameof(implementationType));

            Kind = kind;
            Type = type;
            ImplementationType = implementationType;
        }

        public WorkflowComponentKind Kind { get; }
        public string Type { get; }
        public Type ImplementationType { get; }

        public string? DisplayName { get; set; }
        public string? Description { get; set; }
        public string? Category { get; set; }
        public Type? SettingsType { get; set; }

        // Overrides construction; by default the implementation is built with constructor injection.
        public Func<IServiceProvider, object>? Factory { get; set; }

        public object Create(IServiceProvider services)
        {
            if (Factory is not null)
                return Factory(services);
            _factory ??= ActivatorUtilities.CreateFactory(ImplementationType, System.Type.EmptyTypes);
            return _factory(services, null);
        }

        public JsonNode? GetSettingsSchema()
            => SettingsType is null ? null : WorkflowJson.Options.GetJsonSchemaAsNode(SettingsType);

        public static Type ContractOf(WorkflowComponentKind kind)
            => kind switch
            {
                WorkflowComponentKind.NodeType => typeof(IWorkflowNodeType),
                WorkflowComponentKind.Step => typeof(IWorkflowStep),
                WorkflowComponentKind.Validator => typeof(IWorkflowValidator),
                WorkflowComponentKind.Condition => typeof(IWorkflowCondition),
                WorkflowComponentKind.Output => typeof(IWorkflowOutput),
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
            };
    }
}

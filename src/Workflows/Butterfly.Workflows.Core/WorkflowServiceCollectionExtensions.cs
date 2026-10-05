using System.Reflection;
using Butterfly.Workflows.Components;
using Butterfly.Workflows.Definitions;
using Butterfly.Workflows.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Butterfly.Workflows
{
    public sealed class WorkflowBuilder
    {
        readonly List<WorkflowComponentDescriptor> _descriptors = [];

        internal WorkflowBuilder(IServiceCollection services)
        {
            Services = services;
            AddComponentsFromAssembly(typeof(WorkflowBuilder).Assembly);
        }

        public IServiceCollection Services { get; }
        public WorkflowEngineOptions Options { get; } = new();

        internal IReadOnlyList<WorkflowComponentDescriptor> Descriptors => _descriptors;

        public WorkflowBuilder AddComponent(WorkflowComponentDescriptor descriptor)
        {
            ArgumentNullException.ThrowIfNull(descriptor, nameof(descriptor));
            _descriptors.Add(descriptor);
            return this;
        }

        public WorkflowBuilder AddComponentsFromAssembly(Assembly assembly)
        {
            _descriptors.AddRange(WorkflowComponentCatalog.Discover(assembly));
            return this;
        }

        public WorkflowBuilder AddNodeType<T>(string type, Action<WorkflowComponentDescriptor>? configure = null) where T : class, IWorkflowNodeType
            => Add(WorkflowComponentKind.NodeType, type, typeof(T), configure);

        public WorkflowBuilder AddStep<T>(string type, Action<WorkflowComponentDescriptor>? configure = null) where T : class, IWorkflowStep
            => Add(WorkflowComponentKind.Step, type, typeof(T), configure);

        public WorkflowBuilder AddValidator<T>(string type, Action<WorkflowComponentDescriptor>? configure = null) where T : class, IWorkflowValidator
            => Add(WorkflowComponentKind.Validator, type, typeof(T), configure);

        public WorkflowBuilder AddCondition<T>(string type, Action<WorkflowComponentDescriptor>? configure = null) where T : class, IWorkflowCondition
            => Add(WorkflowComponentKind.Condition, type, typeof(T), configure);

        public WorkflowBuilder AddOutput<T>(string type, Action<WorkflowComponentDescriptor>? configure = null) where T : class, IWorkflowOutput
            => Add(WorkflowComponentKind.Output, type, typeof(T), configure);

        public WorkflowBuilder Configure(Action<WorkflowEngineOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(configure, nameof(configure));
            configure(Options);
            return this;
        }

        public WorkflowBuilder UseDefinitionStore<T>() where T : class, IWorkflowDefinitionStore
        {
            Services.RemoveAll<IWorkflowDefinitionStore>();
            Services.AddSingleton<IWorkflowDefinitionStore, T>();
            return this;
        }

        public WorkflowBuilder UseInstanceStore<T>() where T : class, IWorkflowInstanceStore
        {
            Services.RemoveAll<IWorkflowInstanceStore>();
            Services.AddSingleton<IWorkflowInstanceStore, T>();
            return this;
        }

        WorkflowBuilder Add(WorkflowComponentKind kind, string type, Type implementationType, Action<WorkflowComponentDescriptor>? configure)
        {
            var descriptor = new WorkflowComponentDescriptor(kind, type, implementationType);
            configure?.Invoke(descriptor);
            return AddComponent(descriptor);
        }
    }

    public static class WorkflowServiceCollectionExtensions
    {
        public static IServiceCollection AddWorkflows(this IServiceCollection services, Action<WorkflowBuilder>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(services, nameof(services));
            var builder = new WorkflowBuilder(services);
            configure?.Invoke(builder);

            services.AddSingleton(new WorkflowComponentCatalog(builder.Descriptors));
            services.AddSingleton(builder.Options);
            services.TryAddSingleton(TimeProvider.System);
            services.TryAddSingleton<IWorkflowDefinitionStore, InMemoryWorkflowDefinitionStore>();
            services.TryAddSingleton<IWorkflowInstanceStore, InMemoryWorkflowInstanceStore>();
            services.TryAddSingleton(provider => new WorkflowDefinitionValidator(provider.GetRequiredService<WorkflowComponentCatalog>()));
            services.TryAddSingleton<IWorkflowEngine, WorkflowEngine>();
            return services;
        }
    }
}

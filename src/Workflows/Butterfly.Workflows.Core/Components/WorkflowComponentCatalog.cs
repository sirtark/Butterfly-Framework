using System.Reflection;

namespace Butterfly.Workflows.Components
{
    public sealed class WorkflowComponentCatalog
    {
        readonly Dictionary<string, WorkflowComponentDescriptor> _descriptors = new(StringComparer.OrdinalIgnoreCase);

        // Later descriptors replace earlier ones with the same kind and type, so built-in components can be overridden.
        public WorkflowComponentCatalog(IEnumerable<WorkflowComponentDescriptor> descriptors)
        {
            ArgumentNullException.ThrowIfNull(descriptors, nameof(descriptors));
            foreach (var descriptor in descriptors)
                _descriptors[Key(descriptor.Kind, descriptor.Type)] = descriptor;
        }

        public IReadOnlyCollection<WorkflowComponentDescriptor> Descriptors => _descriptors.Values;

        public IEnumerable<WorkflowComponentDescriptor> OfKind(WorkflowComponentKind kind)
            => _descriptors.Values.Where(descriptor => descriptor.Kind == kind);

        public bool Contains(WorkflowComponentKind kind, string type)
            => _descriptors.ContainsKey(Key(kind, type));

        public bool TryGet(WorkflowComponentKind kind, string type, out WorkflowComponentDescriptor descriptor)
            => _descriptors.TryGetValue(Key(kind, type), out descriptor!);

        public WorkflowComponentDescriptor Get(WorkflowComponentKind kind, string type)
            => TryGet(kind, type, out var descriptor)
                ? descriptor
                : throw new WorkflowException($"No {kind} component is registered with the type '{type}'.");

        public static IEnumerable<WorkflowComponentDescriptor> Discover(Assembly assembly)
        {
            ArgumentNullException.ThrowIfNull(assembly, nameof(assembly));
            foreach (var type in assembly.GetTypes())
            {
                if (type is not { IsClass: true, IsAbstract: false } || type.GetCustomAttribute<WorkflowComponentAttribute>() is not { } attribute)
                    continue;
                foreach (var kind in Enum.GetValues<WorkflowComponentKind>())
                {
                    if (WorkflowComponentDescriptor.ContractOf(kind).IsAssignableFrom(type))
                    {
                        yield return new WorkflowComponentDescriptor(kind, attribute.Type, type)
                        {
                            DisplayName = attribute.DisplayName,
                            Description = attribute.Description,
                            Category = attribute.Category,
                            SettingsType = attribute.SettingsType
                        };
                    }
                }
            }
        }

        static string Key(WorkflowComponentKind kind, string type) => $"{kind}:{type}";
    }
}

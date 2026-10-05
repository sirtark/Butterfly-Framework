using Butterfly.Workflows.Definitions;
using Butterfly.Workflows.Runtime;
using Butterfly.Workflows.Serialization;

namespace Butterfly.Workflows.Persistence
{
    // Keeps JSON rather than objects, so every read is an independent copy, just like a database.
    public sealed class InMemoryWorkflowDefinitionStore : IWorkflowDefinitionStore
    {
        readonly Dictionary<(string Id, int Version), string> _definitions = [];
        readonly Lock _lock = new();

        public ValueTask<WorkflowDefinition?> FindAsync(string id, int? version = null, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                var key = _definitions.Keys
                    .Where(key => key.Id == id && (version is null || key.Version == version))
                    .OrderByDescending(key => key.Version)
                    .FirstOrDefault();
                return ValueTask.FromResult(key.Id is null ? null : WorkflowJson.Deserialize<WorkflowDefinition>(_definitions[key]));
            }
        }

        public ValueTask<IReadOnlyList<WorkflowDefinition>> ListAsync(CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                IReadOnlyList<WorkflowDefinition> latest = [.. _definitions
                    .GroupBy(entry => entry.Key.Id)
                    .Select(group => WorkflowJson.Deserialize<WorkflowDefinition>(group.MaxBy(entry => entry.Key.Version).Value))];
                return ValueTask.FromResult(latest);
            }
        }

        public ValueTask SaveAsync(WorkflowDefinition definition, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(definition, nameof(definition));
            lock (_lock)
                _definitions[(definition.Id, definition.Version)] = WorkflowJson.Serialize(definition);
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> DeleteAsync(string id, int? version = null, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                var keys = _definitions.Keys.Where(key => key.Id == id && (version is null || key.Version == version)).ToList();
                foreach (var key in keys)
                    _definitions.Remove(key);
                return ValueTask.FromResult(keys.Count > 0);
            }
        }
    }

    public sealed class InMemoryWorkflowInstanceStore : IWorkflowInstanceStore
    {
        readonly Dictionary<string, (long Revision, string Json)> _instances = [];
        readonly Lock _lock = new();

        public ValueTask<WorkflowInstance?> FindAsync(string id, CancellationToken cancellationToken = default)
        {
            lock (_lock)
                return ValueTask.FromResult(_instances.TryGetValue(id, out var entry) ? WorkflowJson.Deserialize<WorkflowInstance>(entry.Json) : null);
        }

        public ValueTask<IReadOnlyList<WorkflowInstance>> QueryAsync(WorkflowInstanceQuery query, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(query, nameof(query));
            List<WorkflowInstance> instances;
            lock (_lock)
                instances = [.. _instances.Values.Select(entry => WorkflowJson.Deserialize<WorkflowInstance>(entry.Json))];

            IReadOnlyList<WorkflowInstance> matches = [.. instances
                .Where(instance => query.DefinitionId is null || instance.DefinitionId == query.DefinitionId)
                .Where(instance => query.Status is null || instance.Status == query.Status)
                .Where(instance => query.CorrelationId is null || instance.CorrelationId == query.CorrelationId)
                .Where(instance => query.ElementId is null || instance.Tokens.Any(token => token.NodeId == query.ElementId || token.BranchId == query.ElementId))
                .OrderBy(instance => instance.CreatedAt)
                .Skip(query.Skip)
                .Take(query.Take)];
            return ValueTask.FromResult(matches);
        }

        public ValueTask SaveAsync(WorkflowInstance instance, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(instance, nameof(instance));
            lock (_lock)
            {
                var stored = _instances.TryGetValue(instance.Id, out var entry) ? entry.Revision : 0;
                if (stored != instance.Revision)
                    throw new WorkflowConcurrencyException(instance.Id, instance.Revision, stored);
                instance.Revision = stored + 1;
                _instances[instance.Id] = (instance.Revision, WorkflowJson.Serialize(instance));
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
        {
            lock (_lock)
                return ValueTask.FromResult(_instances.Remove(id));
        }
    }
}

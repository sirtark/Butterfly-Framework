using Butterfly.Workflows.Definitions;
using Butterfly.Workflows.Runtime;

namespace Butterfly.Workflows.Persistence
{
    public interface IWorkflowDefinitionStore
    {
        // Without a version, returns the highest one.
        ValueTask<WorkflowDefinition?> FindAsync(string id, int? version = null, CancellationToken cancellationToken = default);

        // The latest version of every definition.
        ValueTask<IReadOnlyList<WorkflowDefinition>> ListAsync(CancellationToken cancellationToken = default);

        ValueTask SaveAsync(WorkflowDefinition definition, CancellationToken cancellationToken = default);

        // Without a version, deletes every version.
        ValueTask<bool> DeleteAsync(string id, int? version = null, CancellationToken cancellationToken = default);
    }

    public interface IWorkflowInstanceStore
    {
        ValueTask<WorkflowInstance?> FindAsync(string id, CancellationToken cancellationToken = default);
        ValueTask<IReadOnlyList<WorkflowInstance>> QueryAsync(WorkflowInstanceQuery query, CancellationToken cancellationToken = default);

        // Must fail with WorkflowConcurrencyException when the stored revision differs from instance.Revision, and increment it otherwise.
        ValueTask SaveAsync(WorkflowInstance instance, CancellationToken cancellationToken = default);

        ValueTask<bool> DeleteAsync(string id, CancellationToken cancellationToken = default);
    }

    public sealed class WorkflowInstanceQuery
    {
        public string? DefinitionId { get; set; }
        public WorkflowStatus? Status { get; set; }
        public string? CorrelationId { get; set; }

        // Instances with a token at this node or directly in this branch.
        public string? ElementId { get; set; }

        public int Skip { get; set; }
        public int Take { get; set; } = 100;
    }
}

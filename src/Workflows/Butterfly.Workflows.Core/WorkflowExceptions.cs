using Butterfly.Workflows.Definitions;

namespace Butterfly.Workflows
{
    public class WorkflowException(string message, Exception? innerException = null) : Exception(message, innerException);

    public sealed class WorkflowDefinitionException(string definitionId, IReadOnlyList<WorkflowDefinitionError> errors)
        : WorkflowException($"The workflow definition '{definitionId}' is invalid: {string.Join("; ", errors.Select(error => error.Message))}")
    {
        public string DefinitionId { get; } = definitionId;
        public IReadOnlyList<WorkflowDefinitionError> Errors { get; } = errors;
    }

    public sealed class WorkflowNotFoundException(string message) : WorkflowException(message);

    public sealed class WorkflowConcurrencyException(string instanceId, long expectedRevision, long actualRevision)
        : WorkflowException($"The workflow instance '{instanceId}' was saved by someone else (expected revision {expectedRevision}, found {actualRevision}).")
    {
        public string InstanceId { get; } = instanceId;
        public long ExpectedRevision { get; } = expectedRevision;
        public long ActualRevision { get; } = actualRevision;
    }

    // Thrown by validations, or by any component, to reject the current operation without changing the instance.
    public sealed class WorkflowRejectedException(IReadOnlyList<WorkflowError> errors)
        : WorkflowException(string.Join("; ", errors.Select(error => error.Message)))
    {
        public WorkflowRejectedException(string message) : this([new WorkflowError(message)]) { }

        public IReadOnlyList<WorkflowError> Errors { get; } = errors;
    }

    internal sealed class WorkflowComponentException(WorkflowElementDefinition? element, string? stage, string? componentType, Exception innerException)
        : WorkflowException(innerException.Message, innerException)
    {
        public WorkflowElementDefinition? Element { get; } = element;
        public string? ElementId { get; } = element is null ? null : WorkflowDefinitionIndex.KeyOf(element);
        public string? Stage { get; } = stage;
        public string? ComponentType { get; } = componentType;
    }
}

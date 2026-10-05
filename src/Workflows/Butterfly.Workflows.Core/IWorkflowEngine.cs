using System.Text.Json.Nodes;
using Butterfly.Workflows.Definitions;
using Butterfly.Workflows.Runtime;

namespace Butterfly.Workflows
{
    public interface IWorkflowEngine
    {
        Task<WorkflowOperationResult> StartAsync(string definitionId, WorkflowStartOptions? options = null, CancellationToken cancellationToken = default);

        // Saves the definition in the definition store when that id and version are not stored yet.
        Task<WorkflowOperationResult> StartAsync(WorkflowDefinition definition, WorkflowStartOptions? options = null, CancellationToken cancellationToken = default);

        Task<WorkflowOperationResult> PublishAsync(string instanceId, WorkflowEvent workflowEvent, CancellationToken cancellationToken = default);

        // Starts the definitions whose events are marked StartsWorkflow, and publishes to running instances with the same correlation id.
        Task<IReadOnlyList<WorkflowOperationResult>> DispatchAsync(WorkflowEvent workflowEvent, CancellationToken cancellationToken = default);

        Task<WorkflowOperationResult> TriggerTransitionAsync(string instanceId, string transitionId, JsonNode? payload = null, CancellationToken cancellationToken = default);

        Task<WorkflowOperationResult> JumpAsync(string instanceId, string targetId, WorkflowJumpOptions? options = null, CancellationToken cancellationToken = default);

        Task<WorkflowOperationResult> CancelAsync(string instanceId, string? reason = null, CancellationToken cancellationToken = default);
    }

    public sealed class WorkflowStartOptions
    {
        public string? InstanceId { get; set; }
        public int? Version { get; set; }
        public JsonNode? Input { get; set; }
        public JsonObject? Variables { get; set; }
        public string? CorrelationId { get; set; }
        public JsonObject? Metadata { get; set; }
    }

    public sealed class WorkflowJumpOptions
    {
        // Required when more than one token is active.
        public string? TokenId { get; set; }
        public JsonNode? Payload { get; set; }
    }

    public sealed class WorkflowEngineOptions
    {
        public bool RecordHistory { get; set; } = true;

        // Guards against automatic transitions that loop forever without waiting for an event.
        public int MaxActivationsPerOperation { get; set; } = 10_000;
    }
}

using System.Text.Json.Nodes;
using Butterfly.Workflows.Components;
using Butterfly.Workflows.Definitions;
using Butterfly.Workflows.Runtime;

namespace Butterfly.Workflows.AspNetCore
{
    public sealed class WorkflowEventRequest
    {
        public required string Name { get; init; }
        public JsonNode? Payload { get; init; }
        public string? TargetId { get; init; }
        public string? CorrelationId { get; init; }

        public WorkflowEvent ToEvent()
            => new(Name, Payload) { TargetId = TargetId, CorrelationId = CorrelationId };
    }

    public sealed class WorkflowTransitionRequest
    {
        public JsonNode? Payload { get; init; }
    }

    public sealed class WorkflowJumpRequest
    {
        public required string TargetId { get; init; }
        public string? TokenId { get; init; }
        public JsonNode? Payload { get; init; }
    }

    public sealed class WorkflowCancelRequest
    {
        public string? Reason { get; init; }
    }

    public sealed record WorkflowOperationResponse(WorkflowOperationStatus Status, WorkflowInstance? Instance, IReadOnlyList<WorkflowError> Errors)
    {
        public static WorkflowOperationResponse From(WorkflowOperationResult result) => new(result.Status, result.Instance, result.Errors);
    }

    public sealed record WorkflowValidationResponse(bool Valid, IReadOnlyList<WorkflowDefinitionError> Errors);

    public sealed record WorkflowComponentResponse(
        WorkflowComponentKind Kind,
        string Type,
        string? DisplayName,
        string? Description,
        string? Category,
        JsonNode? SettingsSchema)
    {
        public static WorkflowComponentResponse From(WorkflowComponentDescriptor descriptor)
            => new(descriptor.Kind, descriptor.Type, descriptor.DisplayName, descriptor.Description, descriptor.Category, descriptor.GetSettingsSchema());
    }
}

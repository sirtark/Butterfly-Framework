using Butterfly.Chrysalis;
using Butterfly.Workflows.Components;
using Butterfly.Workflows.Definitions;
using Butterfly.Workflows.Graph;
using Butterfly.Workflows.Persistence;
using Butterfly.Workflows.Runtime;
using Butterfly.Workflows.Serialization;
using System.Text.Json.Nodes;

namespace Butterfly.Workflows.Chrysalis
{
    /// <summary>
    /// The workflow API of the ASP.NET Core endpoints as a Chrysalis service (REST routes under /workflows). Definitions and
    /// instances travel as their JSON documents (the shape WorkflowJson writes); requests and results are typed.
    /// </summary>
    [ChrysalisService(Namespace = "butterfly.workflows.v1", Route = "workflows")]
    public interface IWorkflowService
    {
        [HttpGet("definitions")]
        Task<JsonNode> ListDefinitions(CancellationToken cancellationToken);

        [HttpGet("definitions/{id}")]
        Task<JsonNode> GetDefinition(string id, int? version, CancellationToken cancellationToken);

        /// <summary>Creates or replaces a version of a definition after validating it (InvalidArgument lists the problems).</summary>
        [HttpPut("definitions/{id}")]
        Task<JsonNode> SaveDefinition(string id, JsonNode definition, CancellationToken cancellationToken);

        [HttpDelete("definitions/{id}")]
        Task DeleteDefinition(string id, int? version, CancellationToken cancellationToken);

        [HttpPost("definitions/validate")]
        Task<WorkflowValidation> ValidateDefinition(JsonNode definition, CancellationToken cancellationToken);

        [HttpGet("definitions/{id}/graph")]
        Task<JsonNode> GetDefinitionGraph(string id, int? version, CancellationToken cancellationToken);

        [HttpGet("definitions/{id}/mermaid")]
        Task<string> GetDefinitionMermaid(string id, int? version, CancellationToken cancellationToken);

        [HttpPost("definitions/{id}/instances")]
        Task<JsonNode> StartInstance(string id, WorkflowStart? options, CancellationToken cancellationToken);

        [HttpGet("instances")]
        Task<JsonNode> QueryInstances(string? definitionId, WorkflowStatus? status, string? correlationId, string? elementId, int? skip, int? take, CancellationToken cancellationToken);

        [HttpGet("instances/{instanceId}")]
        Task<JsonNode> GetInstance(string instanceId, CancellationToken cancellationToken);

        [HttpGet("instances/{instanceId}/graph")]
        Task<JsonNode> GetInstanceGraph(string instanceId, CancellationToken cancellationToken);

        [HttpGet("instances/{instanceId}/mermaid")]
        Task<string> GetInstanceMermaid(string instanceId, CancellationToken cancellationToken);

        [HttpPost("instances/{instanceId}/events")]
        Task<JsonNode> PublishEvent(string instanceId, WorkflowEventMessage message, CancellationToken cancellationToken);

        [HttpPost("instances/{instanceId}/transitions/{transitionId}")]
        Task<JsonNode> TriggerTransition(string instanceId, string transitionId, JsonNode? payload, CancellationToken cancellationToken);

        [HttpPost("instances/{instanceId}/jump")]
        Task<JsonNode> Jump(string instanceId, string targetId, string? tokenId, JsonNode? payload, CancellationToken cancellationToken);

        [HttpPost("instances/{instanceId}/cancel")]
        Task<JsonNode> Cancel(string instanceId, string? reason, CancellationToken cancellationToken);

        /// <summary>Starts the workflows that begin with the event and publishes it to running instances with its correlation id.</summary>
        [HttpPost("events")]
        Task<IReadOnlyList<WorkflowDispatchResult>> Dispatch(WorkflowEventMessage message, CancellationToken cancellationToken);

        [HttpGet("components")]
        IReadOnlyList<WorkflowComponentInfo> ListComponents(WorkflowComponentKind? kind);
    }

    public sealed record WorkflowStart(string? InstanceId = null, int? Version = null, JsonNode? Input = null, JsonObject? Variables = null,
        string? CorrelationId = null, JsonObject? Metadata = null);

    public sealed record WorkflowEventMessage(string Name, JsonNode? Payload = null, string? TargetId = null, string? CorrelationId = null);

    public sealed record WorkflowValidation(bool Valid, IReadOnlyList<WorkflowDefinitionError> Errors);

    public sealed record WorkflowDispatchResult(WorkflowOperationStatus Status, JsonNode? Instance, IReadOnlyList<WorkflowError> Errors);

    public sealed record WorkflowComponentInfo(WorkflowComponentKind Kind, string Type, string? DisplayName, string? Description, string? Category, JsonNode? SettingsSchema);

    /// <summary>
    /// The service over the registered engine and stores. Outcomes map to Chrysalis statuses as the HTTP endpoints map them:
    /// missing: NotFound; rejected: FailedPrecondition; event not handled: Aborted (409); faulted: Internal with the fault.
    /// </summary>
    public sealed class WorkflowService(
        IWorkflowEngine engine,
        IWorkflowDefinitionStore definitions,
        IWorkflowInstanceStore instances,
        WorkflowDefinitionValidator validator,
        WorkflowComponentCatalog catalog) : IWorkflowService
    {
        public async Task<JsonNode> ListDefinitions(CancellationToken cancellationToken) =>
            ToJson(await definitions.ListAsync(cancellationToken).ConfigureAwait(false));

        public async Task<JsonNode> GetDefinition(string id, int? version, CancellationToken cancellationToken) =>
            ToJson(await FindDefinition(id, version, cancellationToken).ConfigureAwait(false));

        public async Task<JsonNode> SaveDefinition(string id, JsonNode definition, CancellationToken cancellationToken)
        {
            var parsed = ReadDefinition(definition);
            if (string.IsNullOrEmpty(parsed.Id))
                parsed.Id = id;
            else if (parsed.Id != id)
                throw new ChrysalisException(ChrysalisStatus.InvalidArgument, $"The workflow definition id does not match: the call says '{id}' but the definition says '{parsed.Id}'.");

            var errors = validator.Validate(parsed);
            if (errors.Count > 0)
                throw new ChrysalisException(ChrysalisStatus.InvalidArgument, "The workflow definition is not valid: " + string.Join("; ", errors.Select(error => $"{error.Code}: {error.Message}")));

            await definitions.SaveAsync(parsed, cancellationToken).ConfigureAwait(false);
            return ToJson(parsed);
        }

        public async Task DeleteDefinition(string id, int? version, CancellationToken cancellationToken)
        {
            if (!await definitions.DeleteAsync(id, version, cancellationToken).ConfigureAwait(false))
                throw new ChrysalisException(ChrysalisStatus.NotFound, $"The workflow definition '{id}' was not found.");
        }

        public Task<WorkflowValidation> ValidateDefinition(JsonNode definition, CancellationToken cancellationToken)
        {
            var errors = validator.Validate(ReadDefinition(definition));
            return Task.FromResult(new WorkflowValidation(errors.Count == 0, errors));
        }

        public async Task<JsonNode> GetDefinitionGraph(string id, int? version, CancellationToken cancellationToken) =>
            ToJson(WorkflowGraph.Create(await FindDefinition(id, version, cancellationToken).ConfigureAwait(false)));

        public async Task<string> GetDefinitionMermaid(string id, int? version, CancellationToken cancellationToken) =>
            WorkflowGraph.Create(await FindDefinition(id, version, cancellationToken).ConfigureAwait(false)).ToMermaid();

        public async Task<JsonNode> StartInstance(string id, WorkflowStart? options, CancellationToken cancellationToken)
        {
            var start = options is null ? null : new WorkflowStartOptions
            {
                InstanceId = options.InstanceId,
                Version = options.Version,
                Input = options.Input,
                Variables = options.Variables,
                CorrelationId = options.CorrelationId,
                Metadata = options.Metadata
            };
            return Outcome(await engine.StartAsync(id, start, cancellationToken).ConfigureAwait(false));
        }

        public async Task<JsonNode> QueryInstances(string? definitionId, WorkflowStatus? status, string? correlationId, string? elementId, int? skip, int? take, CancellationToken cancellationToken) =>
            ToJson(await instances.QueryAsync(new WorkflowInstanceQuery
            {
                DefinitionId = definitionId,
                Status = status,
                CorrelationId = correlationId,
                ElementId = elementId,
                Skip = skip ?? 0,
                Take = take ?? 100
            }, cancellationToken).ConfigureAwait(false));

        public async Task<JsonNode> GetInstance(string instanceId, CancellationToken cancellationToken) =>
            ToJson(await FindInstance(instanceId, cancellationToken).ConfigureAwait(false));

        public async Task<JsonNode> GetInstanceGraph(string instanceId, CancellationToken cancellationToken) =>
            ToJson(await LiveGraph(instanceId, cancellationToken).ConfigureAwait(false));

        public async Task<string> GetInstanceMermaid(string instanceId, CancellationToken cancellationToken) =>
            (await LiveGraph(instanceId, cancellationToken).ConfigureAwait(false)).ToMermaid();

        public async Task<JsonNode> PublishEvent(string instanceId, WorkflowEventMessage message, CancellationToken cancellationToken) =>
            Outcome(await engine.PublishAsync(instanceId, ToEvent(message), cancellationToken).ConfigureAwait(false));

        public async Task<JsonNode> TriggerTransition(string instanceId, string transitionId, JsonNode? payload, CancellationToken cancellationToken) =>
            Outcome(await engine.TriggerTransitionAsync(instanceId, transitionId, payload, cancellationToken).ConfigureAwait(false));

        public async Task<JsonNode> Jump(string instanceId, string targetId, string? tokenId, JsonNode? payload, CancellationToken cancellationToken) =>
            Outcome(await engine.JumpAsync(instanceId, targetId, new WorkflowJumpOptions { TokenId = tokenId, Payload = payload }, cancellationToken).ConfigureAwait(false));

        public async Task<JsonNode> Cancel(string instanceId, string? reason, CancellationToken cancellationToken) =>
            Outcome(await engine.CancelAsync(instanceId, reason, cancellationToken).ConfigureAwait(false));

        public async Task<IReadOnlyList<WorkflowDispatchResult>> Dispatch(WorkflowEventMessage message, CancellationToken cancellationToken) =>
            [.. (await engine.DispatchAsync(ToEvent(message), cancellationToken).ConfigureAwait(false))
                .Select(result => new WorkflowDispatchResult(result.Status, result.Instance is null ? null : ToJson(result.Instance), result.Errors))];

        public IReadOnlyList<WorkflowComponentInfo> ListComponents(WorkflowComponentKind? kind) =>
            [.. (kind is null ? catalog.Descriptors : catalog.OfKind(kind.Value))
                .OrderBy(descriptor => descriptor.Kind).ThenBy(descriptor => descriptor.Type)
                .Select(descriptor => new WorkflowComponentInfo(descriptor.Kind, descriptor.Type, descriptor.DisplayName, descriptor.Description, descriptor.Category, descriptor.GetSettingsSchema()))];

        private async Task<WorkflowDefinition> FindDefinition(string id, int? version, CancellationToken cancellationToken) =>
            await definitions.FindAsync(id, version, cancellationToken).ConfigureAwait(false)
                ?? throw new ChrysalisException(ChrysalisStatus.NotFound, $"The workflow definition '{id}' was not found.");

        private async Task<WorkflowInstance> FindInstance(string instanceId, CancellationToken cancellationToken) =>
            await instances.FindAsync(instanceId, cancellationToken).ConfigureAwait(false)
                ?? throw new ChrysalisException(ChrysalisStatus.NotFound, $"The workflow instance '{instanceId}' was not found.");

        private async Task<WorkflowGraph> LiveGraph(string instanceId, CancellationToken cancellationToken)
        {
            var instance = await FindInstance(instanceId, cancellationToken).ConfigureAwait(false);
            var definition = await definitions.FindAsync(instance.DefinitionId, instance.DefinitionVersion, cancellationToken).ConfigureAwait(false)
                ?? throw new ChrysalisException(ChrysalisStatus.NotFound, $"The definition of the workflow instance '{instanceId}' was not found.");
            return WorkflowGraph.Create(definition, instance);
        }

        private static JsonNode Outcome(WorkflowOperationResult result) => result.Status switch
        {
            WorkflowOperationStatus.Succeeded => ToJson(result.Instance),
            WorkflowOperationStatus.Rejected => throw new ChrysalisException(ChrysalisStatus.FailedPrecondition,
                "The workflow operation was rejected: " + string.Join("; ", result.Errors.Select(error => error.Message))),
            WorkflowOperationStatus.NotHandled => throw new ChrysalisException(ChrysalisStatus.Aborted, "Nothing in the workflow instance handled the event."),
            _ => throw new ChrysalisException(ChrysalisStatus.Internal, $"The workflow instance faulted: {result.Instance?.Fault?.Message}")
        };

        private static WorkflowEvent ToEvent(WorkflowEventMessage message) =>
            new(message.Name, message.Payload) { TargetId = message.TargetId, CorrelationId = message.CorrelationId };

        private static WorkflowDefinition ReadDefinition(JsonNode definition)
        {
            try
            {
                return WorkflowJson.Deserialize<WorkflowDefinition>(definition.ToJsonString());
            }
            catch (Exception exception) when (exception is System.Text.Json.JsonException or NotSupportedException or InvalidOperationException)
            {
                throw new ChrysalisException(ChrysalisStatus.InvalidArgument, $"The workflow definition cannot be read: {exception.Message}");
            }
        }

        private static JsonNode ToJson<T>(T value) => JsonNode.Parse(WorkflowJson.Serialize(value)) ?? new JsonObject();
    }
}

using System.Text.Json.Nodes;
using Butterfly.Workflows.Components;
using Butterfly.Workflows.Definitions;
using Butterfly.Workflows.Persistence;
using Butterfly.Workflows.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace Butterfly.Workflows
{
    public sealed class WorkflowEngine(
        IServiceScopeFactory scopeFactory,
        IWorkflowDefinitionStore definitions,
        IWorkflowInstanceStore instances,
        WorkflowComponentCatalog catalog,
        WorkflowEngineOptions options,
        TimeProvider time) : IWorkflowEngine
    {
        // Serializes operations on the same instance within this process; the store revision covers other processes.
        readonly SemaphoreSlim[] _gates = [.. Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1))];
        readonly WorkflowDefinitionValidator _validator = new(catalog);

        public async Task<WorkflowOperationResult> StartAsync(string definitionId, WorkflowStartOptions? startOptions = null, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(definitionId, nameof(definitionId));
            var definition = await definitions.FindAsync(definitionId, startOptions?.Version, cancellationToken)
                ?? throw new WorkflowNotFoundException($"The workflow definition '{definitionId}' was not found.");
            _validator.EnsureValid(definition);
            return await StartCoreAsync(definition, startOptions, cancellationToken);
        }

        public async Task<WorkflowOperationResult> StartAsync(WorkflowDefinition definition, WorkflowStartOptions? startOptions = null, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(definition, nameof(definition));
            _validator.EnsureValid(definition);
            if (await definitions.FindAsync(definition.Id, definition.Version, cancellationToken) is null)
                await definitions.SaveAsync(definition, cancellationToken);
            return await StartCoreAsync(definition, startOptions, cancellationToken);
        }

        public Task<WorkflowOperationResult> PublishAsync(string instanceId, WorkflowEvent workflowEvent, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(workflowEvent, nameof(workflowEvent));
            var copy = new WorkflowEvent(workflowEvent.Name, workflowEvent.Payload?.DeepClone())
            {
                TargetId = workflowEvent.TargetId,
                CorrelationId = workflowEvent.CorrelationId
            };
            return RunAsync(instanceId, copy, operation => operation.PublishAsync(), cancellationToken);
        }

        public async Task<IReadOnlyList<WorkflowOperationResult>> DispatchAsync(WorkflowEvent workflowEvent, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(workflowEvent, nameof(workflowEvent));
            var results = new List<WorkflowOperationResult>();

            if (workflowEvent.CorrelationId is not null)
            {
                var running = await instances.QueryAsync(new() { CorrelationId = workflowEvent.CorrelationId, Status = WorkflowStatus.Running, Take = int.MaxValue }, cancellationToken);
                foreach (var instance in running)
                    results.Add(await PublishAsync(instance.Id, workflowEvent, cancellationToken));
            }

            foreach (var definition in await definitions.ListAsync(cancellationToken))
            {
                if (definition.Events.Any(declared => declared.Id == workflowEvent.Name && declared.StartsWorkflow))
                {
                    _validator.EnsureValid(definition);
                    results.Add(await StartCoreAsync(definition, new()
                    {
                        Input = workflowEvent.Payload?.DeepClone(),
                        CorrelationId = workflowEvent.CorrelationId
                    }, cancellationToken));
                }
            }
            return results;
        }

        public Task<WorkflowOperationResult> TriggerTransitionAsync(string instanceId, string transitionId, JsonNode? payload = null, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(transitionId, nameof(transitionId));
            return RunAsync(instanceId, null, operation => operation.TriggerTransitionAsync(transitionId, payload?.DeepClone()), cancellationToken);
        }

        public Task<WorkflowOperationResult> JumpAsync(string instanceId, string targetId, WorkflowJumpOptions? jumpOptions = null, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(targetId, nameof(targetId));
            return RunAsync(instanceId, null, operation => operation.JumpAsync(targetId, jumpOptions?.TokenId, jumpOptions?.Payload?.DeepClone()), cancellationToken);
        }

        public Task<WorkflowOperationResult> CancelAsync(string instanceId, string? reason = null, CancellationToken cancellationToken = default)
            => RunAsync(instanceId, null, operation => operation.CancelAsync(reason), cancellationToken);

        async Task<WorkflowOperationResult> StartCoreAsync(WorkflowDefinition definition, WorkflowStartOptions? startOptions, CancellationToken cancellationToken)
        {
            var now = time.GetUtcNow();
            var variables = definition.Variables?.DeepClone().AsObject() ?? new JsonObject();
            foreach (var (name, value) in startOptions?.Variables ?? new JsonObject())
                variables[name] = value?.DeepClone();

            var instance = new WorkflowInstance
            {
                Id = startOptions?.InstanceId ?? Guid.CreateVersion7().ToString("N"),
                DefinitionId = definition.Id,
                DefinitionVersion = definition.Version,
                Status = WorkflowStatus.Running,
                CorrelationId = startOptions?.CorrelationId,
                Input = startOptions?.Input?.DeepClone(),
                Variables = variables,
                Metadata = startOptions?.Metadata?.DeepClone().AsObject(),
                CreatedAt = now,
                UpdatedAt = now
            };

            var gate = GateFor(instance.Id);
            await gate.WaitAsync(cancellationToken);
            try
            {
                return await ExecuteAsync(definition, instance, null, operation => operation.StartAsync(), isNew: true, cancellationToken);
            }
            finally
            {
                gate.Release();
            }
        }

        async Task<WorkflowOperationResult> RunAsync(string instanceId, WorkflowEvent? workflowEvent, Func<WorkflowOperation, ValueTask<WorkflowOperationStatus>> action, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(instanceId, nameof(instanceId));
            var gate = GateFor(instanceId);
            await gate.WaitAsync(cancellationToken);
            try
            {
                var instance = await instances.FindAsync(instanceId, cancellationToken)
                    ?? throw new WorkflowNotFoundException($"The workflow instance '{instanceId}' was not found.");
                if (instance.Status != WorkflowStatus.Running)
                    return new(WorkflowOperationStatus.Rejected, instance, [new($"The workflow instance '{instanceId}' is {instance.Status}.")]);

                var definition = await definitions.FindAsync(instance.DefinitionId, instance.DefinitionVersion, cancellationToken)
                    ?? throw new WorkflowNotFoundException($"The workflow definition '{instance.DefinitionId}' version {instance.DefinitionVersion} was not found.");
                return await ExecuteAsync(definition, instance, workflowEvent, action, isNew: false, cancellationToken);
            }
            finally
            {
                gate.Release();
            }
        }

        async Task<WorkflowOperationResult> ExecuteAsync(
            WorkflowDefinition definition,
            WorkflowInstance instance,
            WorkflowEvent? workflowEvent,
            Func<WorkflowOperation, ValueTask<WorkflowOperationStatus>> action,
            bool isNew,
            CancellationToken cancellationToken)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var operation = new WorkflowOperation(new WorkflowDefinitionIndex(definition), instance, workflowEvent, scope.ServiceProvider, catalog, options, time, cancellationToken);

            WorkflowOperationStatus status;
            IReadOnlyList<WorkflowError> errors = [];
            try
            {
                status = await action(operation);
            }
            catch (WorkflowRejectedException rejected)
            {
                return new(WorkflowOperationStatus.Rejected, await PersistedAsync(instance.Id, isNew, cancellationToken), rejected.Errors);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await operation.FaultAsync(ex);
                status = WorkflowOperationStatus.Faulted;
                errors = [new(instance.Fault!.Message, instance.Fault.ElementId, instance.Fault.Stage, instance.Fault.ComponentType)];
            }

            if (status == WorkflowOperationStatus.NotHandled)
                return new(status, await PersistedAsync(instance.Id, isNew, cancellationToken));

            instance.UpdatedAt = time.GetUtcNow();
            await instances.SaveAsync(instance, cancellationToken);
            return new(status, instance, errors);
        }

        async ValueTask<WorkflowInstance?> PersistedAsync(string instanceId, bool isNew, CancellationToken cancellationToken)
            => isNew ? null : await instances.FindAsync(instanceId, cancellationToken);

        SemaphoreSlim GateFor(string instanceId)
            => _gates[(StringComparer.Ordinal.GetHashCode(instanceId) & int.MaxValue) % _gates.Length];
    }
}

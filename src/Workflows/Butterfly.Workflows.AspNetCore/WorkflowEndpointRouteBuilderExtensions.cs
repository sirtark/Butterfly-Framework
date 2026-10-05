using System.Diagnostics.CodeAnalysis;
using Butterfly.Workflows.Components;
using Butterfly.Workflows.Definitions;
using Butterfly.Workflows.Graph;
using Butterfly.Workflows.Persistence;
using Butterfly.Workflows.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Butterfly.Workflows.AspNetCore
{
    public static class WorkflowEndpointRouteBuilderExtensions
    {
        // Maps every workflow endpoint under the prefix. The returned group accepts authorization, filters and other conventions.
        public static RouteGroupBuilder MapWorkflowEndpoints(this IEndpointRouteBuilder endpoints, [StringSyntax("Route")] string prefix = "/workflows")
        {
            ArgumentNullException.ThrowIfNull(endpoints, nameof(endpoints));
            var group = endpoints.MapGroup(prefix);
            group.MapWorkflowDefinitionEndpoints("/definitions");
            group.MapWorkflowInstanceEndpoints("/instances");
            group.MapWorkflowEventEndpoints("/events");
            group.MapWorkflowComponentEndpoints("/components");
            return group;
        }

        public static RouteGroupBuilder MapWorkflowDefinitionEndpoints(this IEndpointRouteBuilder endpoints, [StringSyntax("Route")] string prefix = "/workflows/definitions")
        {
            ArgumentNullException.ThrowIfNull(endpoints, nameof(endpoints));
            var group = endpoints.MapGroup(prefix).WithTags("Workflow definitions");

            group.MapGet("", async (IWorkflowDefinitionStore store, CancellationToken cancellationToken)
                    => WorkflowHttp.Json(await store.ListAsync(cancellationToken)))
                .WithName("ListWorkflowDefinitions")
                .WithSummary("Lists the latest version of every workflow definition.")
                .Produces<IReadOnlyList<WorkflowDefinition>>();

            group.MapGet("/{id}", async (string id, int? version, IWorkflowDefinitionStore store, CancellationToken cancellationToken)
                    => await store.FindAsync(id, version, cancellationToken) is { } definition
                        ? WorkflowHttp.Json(definition)
                        : WorkflowHttp.NotFound($"The workflow definition '{id}' was not found."))
                .WithName("GetWorkflowDefinition")
                .WithSummary("Gets a workflow definition; the latest version unless one is given.")
                .Produces<WorkflowDefinition>()
                .ProducesProblem(StatusCodes.Status404NotFound);

            group.MapPut("/{id}", async (string id, HttpContext context, IWorkflowDefinitionStore store, WorkflowDefinitionValidator validator, CancellationToken cancellationToken) =>
                {
                    var (definition, error) = await WorkflowHttp.ReadAsync<WorkflowDefinition>(context.Request, cancellationToken);
                    if (error is not null)
                        return error;
                    if (string.IsNullOrEmpty(definition!.Id))
                        definition.Id = id;
                    else if (definition.Id != id)
                        return WorkflowHttp.Problem(StatusCodes.Status400BadRequest, "The workflow definition id does not match the route.", $"The route says '{id}' but the body says '{definition.Id}'.");

                    var errors = validator.Validate(definition);
                    if (errors.Count > 0)
                        return WorkflowHttp.InvalidDefinition(errors);
                    await store.SaveAsync(definition, cancellationToken);
                    return WorkflowHttp.Json(definition);
                })
                .WithName("SaveWorkflowDefinition")
                .WithSummary("Creates or replaces a version of a workflow definition after validating it.")
                .Accepts<WorkflowDefinition>("application/json")
                .Produces<WorkflowDefinition>()
                .ProducesProblem(StatusCodes.Status400BadRequest);

            group.MapDelete("/{id}", async (string id, int? version, IWorkflowDefinitionStore store, CancellationToken cancellationToken)
                    => await store.DeleteAsync(id, version, cancellationToken)
                        ? Results.NoContent()
                        : WorkflowHttp.NotFound($"The workflow definition '{id}' was not found."))
                .WithName("DeleteWorkflowDefinition")
                .WithSummary("Deletes one version of a workflow definition, or all of them.")
                .ProducesProblem(StatusCodes.Status404NotFound);

            group.MapPost("/validate", async (HttpContext context, WorkflowDefinitionValidator validator, CancellationToken cancellationToken) =>
                {
                    var (definition, error) = await WorkflowHttp.ReadAsync<WorkflowDefinition>(context.Request, cancellationToken);
                    if (error is not null)
                        return error;
                    var errors = validator.Validate(definition!);
                    return WorkflowHttp.Json(new WorkflowValidationResponse(errors.Count == 0, errors));
                })
                .WithName("ValidateWorkflowDefinition")
                .WithSummary("Validates a workflow definition without saving it.")
                .Accepts<WorkflowDefinition>("application/json")
                .Produces<WorkflowValidationResponse>();

            group.MapGet("/{id}/graph", async (string id, int? version, IWorkflowDefinitionStore store, CancellationToken cancellationToken)
                    => await store.FindAsync(id, version, cancellationToken) is { } definition
                        ? WorkflowHttp.Json(WorkflowGraph.Create(definition))
                        : WorkflowHttp.NotFound($"The workflow definition '{id}' was not found."))
                .WithName("GetWorkflowDefinitionGraph")
                .WithSummary("Gets the nodes and edges of a workflow definition, for drawing it.")
                .Produces<WorkflowGraph>()
                .ProducesProblem(StatusCodes.Status404NotFound);

            group.MapGet("/{id}/mermaid", async (string id, int? version, IWorkflowDefinitionStore store, CancellationToken cancellationToken)
                    => await store.FindAsync(id, version, cancellationToken) is { } definition
                        ? Results.Text(WorkflowGraph.Create(definition).ToMermaid(), "text/plain")
                        : WorkflowHttp.NotFound($"The workflow definition '{id}' was not found."))
                .WithName("GetWorkflowDefinitionMermaid")
                .WithSummary("Gets a workflow definition as a Mermaid flowchart.")
                .Produces<string>(contentType: "text/plain")
                .ProducesProblem(StatusCodes.Status404NotFound);

            group.MapPost("/{id}/instances", async (string id, HttpContext context, IWorkflowEngine engine, CancellationToken cancellationToken) =>
                {
                    var (options, error) = await WorkflowHttp.ReadOptionalAsync<WorkflowStartOptions>(context.Request, cancellationToken);
                    if (error is not null)
                        return error;
                    return WorkflowHttp.FromOperation(context, await engine.StartAsync(id, options, cancellationToken), created: true);
                })
                .WithName("StartWorkflowInstance")
                .WithSummary("Starts an instance of a workflow definition.")
                .Accepts<WorkflowStartOptions>("application/json")
                .Produces<WorkflowInstance>(StatusCodes.Status201Created)
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

            return group;
        }

        public static RouteGroupBuilder MapWorkflowInstanceEndpoints(this IEndpointRouteBuilder endpoints, [StringSyntax("Route")] string prefix = "/workflows/instances")
        {
            ArgumentNullException.ThrowIfNull(endpoints, nameof(endpoints));
            var group = endpoints.MapGroup(prefix).WithTags("Workflow instances");

            group.MapGet("", async (string? definitionId, string? status, string? correlationId, string? elementId, int? skip, int? take, IWorkflowInstanceStore store, CancellationToken cancellationToken) =>
                {
                    WorkflowStatus? parsedStatus = null;
                    if (status is not null)
                    {
                        if (!Enum.TryParse<WorkflowStatus>(status, ignoreCase: true, out var value))
                            return WorkflowHttp.Problem(StatusCodes.Status400BadRequest, "Unknown workflow status.", $"'{status}' is not one of {string.Join(", ", Enum.GetNames<WorkflowStatus>())}.");
                        parsedStatus = value;
                    }

                    var query = new WorkflowInstanceQuery
                    {
                        DefinitionId = definitionId,
                        Status = parsedStatus,
                        CorrelationId = correlationId,
                        ElementId = elementId,
                        Skip = skip ?? 0,
                        Take = take ?? 100
                    };
                    return WorkflowHttp.Json(await store.QueryAsync(query, cancellationToken));
                })
                .WithName("QueryWorkflowInstances")
                .WithSummary("Finds workflow instances by definition, status, correlation id or the element their tokens are at.")
                .Produces<IReadOnlyList<WorkflowInstance>>();

            group.MapGet("/{instanceId}", async (string instanceId, IWorkflowInstanceStore store, CancellationToken cancellationToken)
                    => await store.FindAsync(instanceId, cancellationToken) is { } instance
                        ? WorkflowHttp.Json(instance)
                        : WorkflowHttp.NotFound($"The workflow instance '{instanceId}' was not found."))
                .WithName(WorkflowHttp.GetInstanceEndpointName)
                .WithSummary("Gets a workflow instance with its tokens, data and history.")
                .Produces<WorkflowInstance>()
                .ProducesProblem(StatusCodes.Status404NotFound);

            group.MapGet("/{instanceId}/graph", async (string instanceId, IWorkflowInstanceStore instances, IWorkflowDefinitionStore definitions, CancellationToken cancellationToken)
                    => await LiveGraphAsync(instanceId, instances, definitions, cancellationToken) is { } graph
                        ? WorkflowHttp.Json(graph)
                        : WorkflowHttp.NotFound($"The workflow instance '{instanceId}' or its definition was not found."))
                .WithName("GetWorkflowInstanceGraph")
                .WithSummary("Gets the workflow graph with the state of this instance: where its tokens are, what was visited and which transitions were taken.")
                .Produces<WorkflowGraph>()
                .ProducesProblem(StatusCodes.Status404NotFound);

            group.MapGet("/{instanceId}/mermaid", async (string instanceId, IWorkflowInstanceStore instances, IWorkflowDefinitionStore definitions, CancellationToken cancellationToken)
                    => await LiveGraphAsync(instanceId, instances, definitions, cancellationToken) is { } graph
                        ? Results.Text(graph.ToMermaid(), "text/plain")
                        : WorkflowHttp.NotFound($"The workflow instance '{instanceId}' or its definition was not found."))
                .WithName("GetWorkflowInstanceMermaid")
                .WithSummary("Gets the workflow graph with the state of this instance as a Mermaid flowchart.")
                .Produces<string>(contentType: "text/plain")
                .ProducesProblem(StatusCodes.Status404NotFound);

            group.MapPost("/{instanceId}/events", async (string instanceId, HttpContext context, IWorkflowEngine engine, CancellationToken cancellationToken) =>
                {
                    var (request, error) = await WorkflowHttp.ReadAsync<WorkflowEventRequest>(context.Request, cancellationToken);
                    if (error is not null)
                        return error;
                    return WorkflowHttp.FromOperation(context, await engine.PublishAsync(instanceId, request!.ToEvent(), cancellationToken));
                })
                .WithName("PublishWorkflowEvent")
                .WithSummary("Publishes an event to a workflow instance.")
                .Accepts<WorkflowEventRequest>("application/json")
                .Produces<WorkflowInstance>()
                .ProducesProblem(StatusCodes.Status409Conflict)
                .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

            group.MapPost("/{instanceId}/transitions/{transitionId}", async (string instanceId, string transitionId, HttpContext context, IWorkflowEngine engine, CancellationToken cancellationToken) =>
                {
                    var (request, error) = await WorkflowHttp.ReadOptionalAsync<WorkflowTransitionRequest>(context.Request, cancellationToken);
                    if (error is not null)
                        return error;
                    return WorkflowHttp.FromOperation(context, await engine.TriggerTransitionAsync(instanceId, transitionId, request?.Payload, cancellationToken));
                })
                .WithName("TriggerWorkflowTransition")
                .WithSummary("Takes a manual transition.")
                .Accepts<WorkflowTransitionRequest>("application/json")
                .Produces<WorkflowInstance>()
                .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

            group.MapPost("/{instanceId}/jump", async (string instanceId, HttpContext context, IWorkflowEngine engine, CancellationToken cancellationToken) =>
                {
                    var (request, error) = await WorkflowHttp.ReadAsync<WorkflowJumpRequest>(context.Request, cancellationToken);
                    if (error is not null)
                        return error;
                    var options = new WorkflowJumpOptions { TokenId = request!.TokenId, Payload = request.Payload };
                    return WorkflowHttp.FromOperation(context, await engine.JumpAsync(instanceId, request.TargetId, options, cancellationToken));
                })
                .WithName("JumpWorkflowInstance")
                .WithSummary("Moves a token to any node or branch, when the definition allows jumps.")
                .Accepts<WorkflowJumpRequest>("application/json")
                .Produces<WorkflowInstance>()
                .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

            group.MapPost("/{instanceId}/cancel", async (string instanceId, HttpContext context, IWorkflowEngine engine, CancellationToken cancellationToken) =>
                {
                    var (request, error) = await WorkflowHttp.ReadOptionalAsync<WorkflowCancelRequest>(context.Request, cancellationToken);
                    if (error is not null)
                        return error;
                    return WorkflowHttp.FromOperation(context, await engine.CancelAsync(instanceId, request?.Reason, cancellationToken));
                })
                .WithName("CancelWorkflowInstance")
                .WithSummary("Cancels a running workflow instance.")
                .Accepts<WorkflowCancelRequest>("application/json")
                .Produces<WorkflowInstance>()
                .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

            return group;
        }

        public static RouteGroupBuilder MapWorkflowEventEndpoints(this IEndpointRouteBuilder endpoints, [StringSyntax("Route")] string prefix = "/workflows/events")
        {
            ArgumentNullException.ThrowIfNull(endpoints, nameof(endpoints));
            var group = endpoints.MapGroup(prefix).WithTags("Workflow events");

            group.MapPost("", async (HttpContext context, IWorkflowEngine engine, CancellationToken cancellationToken) =>
                {
                    var (request, error) = await WorkflowHttp.ReadAsync<WorkflowEventRequest>(context.Request, cancellationToken);
                    if (error is not null)
                        return error;
                    var results = await engine.DispatchAsync(request!.ToEvent(), cancellationToken);
                    return WorkflowHttp.Json(results.Select(WorkflowOperationResponse.From).ToList());
                })
                .WithName("DispatchWorkflowEvent")
                .WithSummary("Starts the workflows that begin with this event and publishes it to running instances with the same correlation id.")
                .Accepts<WorkflowEventRequest>("application/json")
                .Produces<IReadOnlyList<WorkflowOperationResponse>>();

            return group;
        }

        public static RouteGroupBuilder MapWorkflowComponentEndpoints(this IEndpointRouteBuilder endpoints, [StringSyntax("Route")] string prefix = "/workflows/components")
        {
            ArgumentNullException.ThrowIfNull(endpoints, nameof(endpoints));
            var group = endpoints.MapGroup(prefix).WithTags("Workflow components");

            group.MapGet("", (string? kind, WorkflowComponentCatalog catalog) =>
                {
                    IEnumerable<WorkflowComponentDescriptor> descriptors = catalog.Descriptors;
                    if (kind is not null)
                    {
                        if (!Enum.TryParse<WorkflowComponentKind>(kind, ignoreCase: true, out var parsedKind))
                            return WorkflowHttp.Problem(StatusCodes.Status400BadRequest, "Unknown component kind.", $"'{kind}' is not one of {string.Join(", ", Enum.GetNames<WorkflowComponentKind>())}.");
                        descriptors = catalog.OfKind(parsedKind);
                    }
                    return WorkflowHttp.Json(descriptors.OrderBy(descriptor => descriptor.Kind).ThenBy(descriptor => descriptor.Type).Select(WorkflowComponentResponse.From).ToList());
                })
                .WithName("ListWorkflowComponents")
                .WithSummary("Lists the registered node types, steps, validators, conditions and outputs, with the JSON schema of their settings.")
                .Produces<IReadOnlyList<WorkflowComponentResponse>>();

            return group;
        }

        static async Task<WorkflowGraph?> LiveGraphAsync(string instanceId, IWorkflowInstanceStore instances, IWorkflowDefinitionStore definitions, CancellationToken cancellationToken)
        {
            if (await instances.FindAsync(instanceId, cancellationToken) is not { } instance)
                return null;
            var definition = await definitions.FindAsync(instance.DefinitionId, instance.DefinitionVersion, cancellationToken);
            return definition is null ? null : WorkflowGraph.Create(definition, instance);
        }
    }
}

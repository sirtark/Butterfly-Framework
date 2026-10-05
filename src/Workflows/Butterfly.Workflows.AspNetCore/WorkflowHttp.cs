using System.Text.Json;
using Butterfly.Workflows.Definitions;
using Butterfly.Workflows.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Butterfly.Workflows.AspNetCore
{
    // Bodies are read and written with WorkflowJson.Options, so the API speaks the same JSON as the stores, whatever the app's JSON settings are.
    internal static class WorkflowHttp
    {
        internal const string GetInstanceEndpointName = "GetWorkflowInstance";

        public static IResult Json(object? value, int statusCode = StatusCodes.Status200OK)
            => Results.Json(value, WorkflowJson.Options, statusCode: statusCode);

        public static async ValueTask<(T? Value, IResult? Error)> ReadAsync<T>(HttpRequest request, CancellationToken cancellationToken) where T : class
        {
            if (!request.HasJsonContentType())
                return (null, Problem(StatusCodes.Status415UnsupportedMediaType, "The request body must be JSON."));
            try
            {
                var value = await request.ReadFromJsonAsync<T>(WorkflowJson.Options, cancellationToken);
                return value is null ? (null, Problem(StatusCodes.Status400BadRequest, "The request body is empty.")) : (value, null);
            }
            catch (JsonException ex)
            {
                return (null, Problem(StatusCodes.Status400BadRequest, "The request body is not valid.", ex.Message));
            }
        }

        public static async ValueTask<(T? Value, IResult? Error)> ReadOptionalAsync<T>(HttpRequest request, CancellationToken cancellationToken) where T : class
            => request.ContentLength is 0 || (request.ContentLength is null && !request.HasJsonContentType())
                ? (null, null)
                : await ReadAsync<T>(request, cancellationToken);

        public static IResult FromOperation(HttpContext context, WorkflowOperationResult result, bool created = false)
        {
            switch (result.Status)
            {
                case WorkflowOperationStatus.Succeeded:
                    if (!created)
                        return Json(result.Instance);
                    var location = context.RequestServices.GetService<LinkGenerator>()?.GetPathByName(context, GetInstanceEndpointName, new { instanceId = result.Instance!.Id });
                    if (location is not null)
                        context.Response.Headers.Location = location;
                    return Json(result.Instance, StatusCodes.Status201Created);

                case WorkflowOperationStatus.Rejected:
                    return Problem(StatusCodes.Status422UnprocessableEntity, "The workflow operation was rejected.", string.Join("; ", result.Errors.Select(error => error.Message)), new()
                    {
                        ["instanceId"] = result.Instance?.Id,
                        ["errors"] = result.Errors
                    });

                case WorkflowOperationStatus.NotHandled:
                    return Problem(StatusCodes.Status409Conflict, "Nothing in the workflow instance handled the event.", extensions: new()
                    {
                        ["instanceId"] = result.Instance?.Id
                    });

                default:
                    var fault = result.Instance?.Fault;
                    return Problem(StatusCodes.Status500InternalServerError, "The workflow instance faulted.", fault?.Message, new()
                    {
                        ["instanceId"] = result.Instance?.Id,
                        ["elementId"] = fault?.ElementId,
                        ["stage"] = fault?.Stage,
                        ["componentType"] = fault?.ComponentType
                    });
            }
        }

        public static IResult InvalidDefinition(IReadOnlyList<WorkflowDefinitionError> errors)
            => Problem(StatusCodes.Status400BadRequest, "The workflow definition is invalid.", string.Join("; ", errors.Select(error => error.Message)), new()
            {
                ["errors"] = errors
            });

        public static IResult NotFound(string detail)
            => Problem(StatusCodes.Status404NotFound, "Not found.", detail);

        public static IResult Problem(int statusCode, string title, string? detail = null, Dictionary<string, object?>? extensions = null)
            => Results.Problem(detail, statusCode: statusCode, title: title, extensions: extensions);
    }
}

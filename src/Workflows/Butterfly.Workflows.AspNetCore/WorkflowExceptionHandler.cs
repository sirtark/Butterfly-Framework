using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Butterfly.Workflows.AspNetCore
{
    internal sealed class WorkflowExceptionHandler(IProblemDetailsService problemDetailsService) : IExceptionHandler
    {
        public ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
        {
            if (exception is not WorkflowException workflowException)
                return ValueTask.FromResult(false);

            var (status, title) = workflowException switch
            {
                WorkflowNotFoundException => (StatusCodes.Status404NotFound, "Workflow not found"),
                WorkflowDefinitionException => (StatusCodes.Status400BadRequest, "Invalid workflow definition"),
                WorkflowConcurrencyException => (StatusCodes.Status409Conflict, "Workflow instance changed concurrently"),
                WorkflowRejectedException => (StatusCodes.Status422UnprocessableEntity, "Workflow operation rejected"),
                _ => (StatusCodes.Status500InternalServerError, "Workflow error")
            };

            var problem = new ProblemDetails { Status = status, Title = title, Detail = workflowException.Message };
            switch (workflowException)
            {
                case WorkflowDefinitionException definition:
                    problem.Extensions["definitionId"] = definition.DefinitionId;
                    problem.Extensions["errors"] = definition.Errors;
                    break;
                case WorkflowConcurrencyException concurrency:
                    problem.Extensions["instanceId"] = concurrency.InstanceId;
                    break;
                case WorkflowRejectedException rejected:
                    problem.Extensions["errors"] = rejected.Errors;
                    break;
            }

            httpContext.Response.StatusCode = status;
            return problemDetailsService.TryWriteAsync(new ProblemDetailsContext { HttpContext = httpContext, ProblemDetails = problem, Exception = exception });
        }
    }
}

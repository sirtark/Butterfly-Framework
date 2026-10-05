using Microsoft.Extensions.DependencyInjection;

namespace Butterfly.Workflows.AspNetCore
{
    public static class WorkflowProblemDetailsExtensions
    {
        public static IServiceCollection AddWorkflowProblemDetails(this IServiceCollection services)
        {
            services.AddProblemDetails();
            services.AddExceptionHandler<WorkflowExceptionHandler>();
            return services;
        }
    }
}

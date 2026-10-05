using Microsoft.Extensions.DependencyInjection;

namespace Butterfly.Scripting.AspNetCore
{
    public static class ScriptProblemDetailsExtensions
    {
        public static IServiceCollection AddScriptProblemDetails(this IServiceCollection services)
        {
            services.AddProblemDetails();
            services.AddExceptionHandler<ScriptExceptionHandler>();
            return services;
        }
    }
}

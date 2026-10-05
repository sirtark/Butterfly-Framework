using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Butterfly.Scripting.AspNetCore
{
    internal sealed class ScriptExceptionHandler(IProblemDetailsService problemDetailsService) : IExceptionHandler
    {
        public ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
        {
            if (exception is not ScriptException scriptException)
                return ValueTask.FromResult(false);

            var (status, title) = scriptException switch
            {
                UnsupportedScriptLanguageException => (StatusCodes.Status400BadRequest, "Unsupported scripting language"),
                ScriptValidationException => (StatusCodes.Status400BadRequest, "Invalid script"),
                ScriptCompilationException => (StatusCodes.Status400BadRequest, "Script compilation failed"),
                ScriptBindingException => (StatusCodes.Status422UnprocessableEntity, "Script parameter binding failed"),
                ScriptTimeoutException => (StatusCodes.Status504GatewayTimeout, "Script timed out"),
                _ => (StatusCodes.Status500InternalServerError, "Script execution failed")
            };

            var problem = new ProblemDetails { Status = status, Title = title, Detail = scriptException.Message };
            problem.Extensions["language"] = scriptException.Language.ToString();
            switch (scriptException)
            {
                case ScriptValidationException validation:
                    problem.Extensions["errors"] = validation.Errors;
                    break;
                case ScriptCompilationException compilation:
                    problem.Extensions["errors"] = compilation.Diagnostics;
                    break;
                case ScriptBindingException binding:
                    problem.Extensions["parameter"] = binding.ParameterName;
                    break;
            }

            httpContext.Response.StatusCode = status;
            return problemDetailsService.TryWriteAsync(new ProblemDetailsContext { HttpContext = httpContext, ProblemDetails = problem, Exception = exception });
        }
    }
}

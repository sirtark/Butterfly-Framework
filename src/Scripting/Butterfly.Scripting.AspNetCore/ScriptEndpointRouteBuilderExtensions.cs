using Butterfly.Scripting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Butterfly.Scripting.AspNetCore
{
    public static class ScriptEndpointRouteBuilderExtensions
    {
        public static RouteHandlerBuilder MapScriptEndpoint(this IEndpointRouteBuilder endpoints, [StringSyntax("Route")] string pattern, ScriptingLanguage language, string? invokerKey = null)
        {
            ArgumentNullException.ThrowIfNull(endpoints, nameof(endpoints));
            return endpoints.MapPost(pattern, async (ScriptInvocationRequest request, HttpContext context) =>
            {
                var services = context.RequestServices;
                var invoker = invokerKey is null
                    ? services.GetRequiredService<IScriptInvoker>()
                    : services.GetRequiredKeyedService<IScriptInvoker>(invokerKey);

                var result = await invoker.InvokeScriptAsync(language, request.Source, ToParameters(request.Parameters), services, context.RequestAborted);
                return Results.Ok(result);
            });
        }

        static IReadOnlyDictionary<string, object> ToParameters(Dictionary<string, JsonElement>? parameters)
            => parameters is null
                ? ScriptParameters.Empty
                : parameters.ToDictionary(parameter => parameter.Key, parameter => ToValue(parameter.Value)!);

        static object? ToValue(JsonElement element)
            => element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number when element.TryGetInt32(out var int32) => int32,
                JsonValueKind.Number when element.TryGetInt64(out var int64) => int64,
                JsonValueKind.Number when element.TryGetDecimal(out var @decimal) => @decimal,
                JsonValueKind.Number => element.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => element.Clone()
            };
    }
}

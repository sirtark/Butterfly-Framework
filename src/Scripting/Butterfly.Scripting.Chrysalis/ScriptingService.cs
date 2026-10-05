using Butterfly.Chrysalis;

namespace Butterfly.Scripting.Chrysalis
{
    /// <summary>
    /// Runs scripts remotely, like MapScriptEndpoint does in ASP.NET Core: POST scripts/execute over REST. Exposing it lets
    /// callers run code on the server: protect it with authentication middleware and a restricted script configuration.
    /// </summary>
    [ChrysalisService(Namespace = "butterfly.scripting.v1", Route = "scripts")]
    public interface IScriptingService
    {
        /// <returns>The value of the script, as a dynamic value (numbers, text, lists, objects...).</returns>
        [HttpPost("execute")]
        Task<object?> Execute(ScriptRequest request, CancellationToken cancellationToken);
    }

    /// <param name="Parameters">Values the script sees by name (null values are left out).</param>
    public sealed record ScriptRequest(ScriptingLanguage Language, string Source, Dictionary<string, object?>? Parameters = null);

    /// <summary>
    /// Runs scripts with the registered <see cref="IScriptInvoker"/>; script errors become Chrysalis statuses
    /// (compilation and validation: InvalidArgument, timeout: DeadlineExceeded, unknown language: Unimplemented).
    /// </summary>
    public sealed class ScriptingService(IScriptInvoker invoker, IServiceProvider services) : IScriptingService
    {
        public async Task<object?> Execute(ScriptRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (string.IsNullOrWhiteSpace(request.Source))
                throw new ChrysalisException(ChrysalisStatus.InvalidArgument, "The script source is empty.");

            IReadOnlyDictionary<string, object> parameters = request.Parameters is null
                ? ScriptParameters.Empty
                : request.Parameters.Where(parameter => parameter.Value is not null).ToDictionary(parameter => parameter.Key, parameter => parameter.Value!);
            try
            {
                return await invoker.InvokeScriptAsync(request.Language, request.Source, parameters, services, cancellationToken).ConfigureAwait(false);
            }
            catch (ScriptException exception)
            {
                throw new ChrysalisException(exception switch
                {
                    ScriptCompilationException or ScriptValidationException or ScriptBindingException => ChrysalisStatus.InvalidArgument,
                    ScriptTimeoutException => ChrysalisStatus.DeadlineExceeded,
                    UnsupportedScriptLanguageException => ChrysalisStatus.Unimplemented,
                    _ => ChrysalisStatus.Aborted
                }, exception.Message, exception);
            }
        }
    }
}

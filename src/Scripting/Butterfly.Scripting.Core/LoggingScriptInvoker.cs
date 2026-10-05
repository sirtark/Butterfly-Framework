using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace Butterfly.Scripting
{
    internal sealed class LoggingScriptInvoker(IScriptInvoker inner, ILogger logger) : IScriptInvoker
    {
        public async Task<object?> InvokeScriptAsync(ScriptingLanguage language, string scriptContent, IReadOnlyDictionary<string, object> parameters, IServiceProvider? services = null, CancellationToken cancellationToken = default)
        {
            var start = Stopwatch.GetTimestamp();
            try
            {
                var result = await inner.InvokeScriptAsync(language, scriptContent, parameters, services, cancellationToken).ConfigureAwait(false);
                logger.LogDebug("{Language} script executed in {ElapsedMilliseconds} ms.", language, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                return result;
            }
            catch (ScriptException ex)
            {
                logger.LogWarning(ex, "{Language} script was rejected after {ElapsedMilliseconds} ms.", language, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "{Language} script failed after {ElapsedMilliseconds} ms.", language, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                throw;
            }
        }
    }
}

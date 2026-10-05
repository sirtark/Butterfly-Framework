using System.Diagnostics;

namespace Butterfly.Scripting
{
    internal sealed class TelemetryScriptInvoker(IScriptInvoker inner) : IScriptInvoker
    {
        public async Task<object?> InvokeScriptAsync(ScriptingLanguage language, string scriptContent, IReadOnlyDictionary<string, object> parameters, IServiceProvider? services = null, CancellationToken cancellationToken = default)
        {
            using var activity = ScriptingTelemetry.ActivitySource.StartActivity("script.invoke");
            activity?.SetTag("script.language", language.ToString());
            var start = Stopwatch.GetTimestamp();
            var outcome = "success";
            try
            {
                return await inner.InvokeScriptAsync(language, scriptContent, parameters, services, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                outcome = ex switch
                {
                    ScriptTimeoutException => "timeout",
                    ScriptException => "rejected",
                    OperationCanceledException => "canceled",
                    _ => "error"
                };
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                throw;
            }
            finally
            {
                var tags = new TagList { { "script.language", language.ToString() }, { "script.outcome", outcome } };
                ScriptingTelemetry.Invocations.Add(1, tags);
                ScriptingTelemetry.Duration.Record(Stopwatch.GetElapsedTime(start).TotalMilliseconds, tags);
            }
        }
    }
}

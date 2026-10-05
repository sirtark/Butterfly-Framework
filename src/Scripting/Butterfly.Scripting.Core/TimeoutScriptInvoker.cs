namespace Butterfly.Scripting
{
    internal sealed class TimeoutScriptInvoker(IScriptInvoker inner, TimeSpan timeout) : IScriptInvoker
    {
        public async Task<object?> InvokeScriptAsync(ScriptingLanguage language, string scriptContent, IReadOnlyDictionary<string, object> parameters, IServiceProvider? services = null, CancellationToken cancellationToken = default)
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            try
            {
                // Task.Run keeps a script stuck in synchronous code from blocking the caller past the timeout.
                return await Task.Run(() => inner.InvokeScriptAsync(language, scriptContent, parameters, services, timeoutSource.Token), timeoutSource.Token)
                    .WaitAsync(timeoutSource.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new ScriptTimeoutException(language, timeout);
            }
        }
    }
}

namespace Butterfly.Scripting
{
    public sealed class BuiltScriptInvoker : IScriptInvoker
    {
        readonly Dictionary<ScriptingLanguage, IScriptExecutor> _executors;

        internal BuiltScriptInvoker(Dictionary<ScriptingLanguage, IScriptExecutor> executors)
        {
            _executors = executors ?? throw new ArgumentNullException(nameof(executors));
        }

        public Task<object?> InvokeScriptAsync(ScriptingLanguage language, string scriptContent, IReadOnlyDictionary<string, object> parameters, IServiceProvider? services = null, CancellationToken cancellationToken = default)
            => ScriptExecution.InvokeAsync(_executors, language, scriptContent, parameters, services, cancellationToken);
    }
}

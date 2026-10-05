namespace Butterfly.Scripting
{
    internal sealed class ServiceProviderScriptInvoker(IScriptInvoker inner, IServiceProvider scopeServices) : IScriptInvoker
    {
        public Task<object?> InvokeScriptAsync(ScriptingLanguage language, string scriptContent, IReadOnlyDictionary<string, object> parameters, IServiceProvider? services = null, CancellationToken cancellationToken = default)
            => inner.InvokeScriptAsync(language, scriptContent, parameters, services ?? scopeServices, cancellationToken);
    }
}

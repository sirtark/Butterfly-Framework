namespace Butterfly.Scripting
{
    public interface IScriptInvoker
    {
        Task<object?> InvokeScriptAsync(ScriptingLanguage language, string scriptContent, IReadOnlyDictionary<string, object> parameters, IServiceProvider? services = null, CancellationToken cancellationToken = default);
    }
}

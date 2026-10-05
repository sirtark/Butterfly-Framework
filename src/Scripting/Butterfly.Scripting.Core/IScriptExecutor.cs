namespace Butterfly.Scripting
{
    public interface IScriptExecutor
    {
        ScriptingLanguage Lenguage { get; }
        Task<object?> ExecuteAsync(string scriptContent, IReadOnlyDictionary<string, object> parameters, IServiceProvider? services = null, CancellationToken cancellationToken = default);
        ValueTask<ScriptValidationResult> ValidateAsync(string scriptContent, CancellationToken cancellationToken = default);
    }
}

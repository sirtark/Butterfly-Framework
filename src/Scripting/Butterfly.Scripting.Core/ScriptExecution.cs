namespace Butterfly.Scripting
{
    internal static class ScriptExecution
    {
        internal static async Task<object?> InvokeAsync(IReadOnlyDictionary<ScriptingLanguage, IScriptExecutor> executors, ScriptingLanguage language, string scriptContent, IReadOnlyDictionary<string, object> parameters, IServiceProvider? services, CancellationToken cancellationToken)
        {
            if (!executors.TryGetValue(language, out var executor))
                throw new UnsupportedScriptLanguageException(language);

            var validation = await executor.ValidateAsync(scriptContent, cancellationToken).ConfigureAwait(false);
            if (!validation.IsValid)
                throw new ScriptValidationException(language, validation.Errors);

            return await executor.ExecuteAsync(scriptContent, parameters, services, cancellationToken).ConfigureAwait(false);
        }
    }
}

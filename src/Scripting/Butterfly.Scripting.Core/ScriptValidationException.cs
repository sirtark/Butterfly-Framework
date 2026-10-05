namespace Butterfly.Scripting
{
    public sealed class ScriptValidationException(ScriptingLanguage language, IReadOnlyList<string> errors)
        : ScriptException(language, $"Invalid {language} script: {string.Join("; ", errors)}")
    {
        public IReadOnlyList<string> Errors { get; } = errors;
    }
}

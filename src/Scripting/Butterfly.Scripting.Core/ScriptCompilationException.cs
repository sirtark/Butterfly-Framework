namespace Butterfly.Scripting
{
    public sealed class ScriptCompilationException(ScriptingLanguage language, IReadOnlyList<string> diagnostics)
        : ScriptException(language, $"The {language} script failed to compile: {string.Join("; ", diagnostics)}")
    {
        public IReadOnlyList<string> Diagnostics { get; } = diagnostics;
    }
}

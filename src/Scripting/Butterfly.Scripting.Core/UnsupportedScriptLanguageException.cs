namespace Butterfly.Scripting
{
    public sealed class UnsupportedScriptLanguageException(ScriptingLanguage language)
        : ScriptException(language, $"Unsupported scripting language: {language}.");
}

namespace Butterfly.Scripting
{
    public class ScriptException(ScriptingLanguage language, string message, Exception? innerException = null) : Exception(message, innerException)
    {
        public ScriptingLanguage Language { get; } = language;
    }
}

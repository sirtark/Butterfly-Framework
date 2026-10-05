namespace Butterfly.Scripting
{
    public sealed class ScriptBindingException(ScriptingLanguage language, string parameterName, string message, Exception? innerException = null)
        : ScriptException(language, message, innerException)
    {
        public string ParameterName { get; } = parameterName;
    }
}

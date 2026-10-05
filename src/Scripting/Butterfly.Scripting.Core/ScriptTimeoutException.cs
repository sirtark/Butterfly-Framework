using System.Globalization;

namespace Butterfly.Scripting
{
    public sealed class ScriptTimeoutException(ScriptingLanguage language, TimeSpan timeout)
        : ScriptException(language, $"The {language} script did not finish within {timeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)} s.")
    {
        public TimeSpan Timeout { get; } = timeout;
    }
}

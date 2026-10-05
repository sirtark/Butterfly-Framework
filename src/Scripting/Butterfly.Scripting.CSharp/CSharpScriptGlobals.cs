namespace Butterfly.Scripting.CSharp
{
    public sealed class CSharpScriptGlobals(IReadOnlyDictionary<string, object> parameters)
    {
        public IReadOnlyDictionary<string, object> Parameters { get; } = parameters ?? throw new ArgumentNullException(nameof(parameters));
    }
}

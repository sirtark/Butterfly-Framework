using System.Dynamic;

namespace Butterfly.Scripting.CSharp
{
    public sealed class CSharpDynamicScriptGlobals(IReadOnlyDictionary<string, object> parameters)
    {
        public dynamic Parameters { get; } = ToExpando(parameters ?? throw new ArgumentNullException(nameof(parameters)));

        static ExpandoObject ToExpando(IReadOnlyDictionary<string, object> parameters)
        {
            var expando = new ExpandoObject();
            var members = (IDictionary<string, object?>)expando;
            foreach (var (name, value) in parameters)
                members[name] = value;
            return expando;
        }
    }
}

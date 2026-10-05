using Microsoft.CodeAnalysis.Scripting;
using System.Dynamic;
using System.Reflection;

namespace Butterfly.Scripting.CSharp
{
    public sealed class CSharpScriptConfig
    {
        public IReadOnlyList<Assembly> References { get; init; } = [];
        public IReadOnlyList<string> Imports { get; init; } = [];
        public CSharpScriptMode Mode { get; init; } = CSharpScriptMode.Parameters;
        public CSharpScriptFlags Flags { get; init; } = CSharpScriptFlags.None;
        public Type? ContextType { get; init; }
        public int CompilationCacheSize { get; init; }

        public static CSharpScriptConfig Default => new CSharpScriptConfigBuilder().WithDefaults().Build();

        internal ScriptOptions ToScriptOptions()
        {
            var options = ScriptOptions.Default
                .WithReferences(References)
                .WithImports(Imports);

            return Mode == CSharpScriptMode.Dynamic
                ? options.AddReferences(typeof(Microsoft.CSharp.RuntimeBinder.Binder).Assembly, typeof(ExpandoObject).Assembly)
                : options;
        }
    }
}

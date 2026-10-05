using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Immutable;

namespace Butterfly.Serialization.Generators
{
    /// <summary>
    /// Describes, at compile time: types marked with [SerializationContract], type arguments of ButterflySerializer calls,
    /// and interfaces marked with [ChrysalisService] (operations, invokers and a typed client). Nothing is discovered at run
    /// time, so serialization needs no reflection and works with Native AOT.
    /// </summary>
    [Generator(LanguageNames.CSharp)]
    public sealed class SerializationGenerator : IIncrementalGenerator
    {
        internal const string ServiceAttribute = "Butterfly.Chrysalis.ChrysalisServiceAttribute";
        internal const string ContractAttribute = "Butterfly.Serialization.SerializationContractAttribute";
        internal const string Serializer = "Butterfly.Serialization.ButterflySerializer";

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var services = context.SyntaxProvider.ForAttributeWithMetadataName(
                    ServiceAttribute,
                    static (node, _) => node is InterfaceDeclarationSyntax,
                    static (syntax, _) => (ITypeSymbol)syntax.TargetSymbol)
                .Collect();

            var contracts = context.SyntaxProvider.ForAttributeWithMetadataName(
                    ContractAttribute,
                    static (node, _) => node is TypeDeclarationSyntax,
                    static (syntax, _) => (ITypeSymbol)syntax.TargetSymbol)
                .Collect();

            // ButterflySerializer.Serialize<T>/SerializeToString<T>/Deserialize<T>, with explicit or inferred T.
            var usages = context.SyntaxProvider.CreateSyntaxProvider(
                    static (node, _) => node is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "Serialize" or "SerializeToString" or "Deserialize" } },
                    static (syntax, cancellationToken) =>
                        syntax.SemanticModel.GetSymbolInfo(syntax.Node, cancellationToken).Symbol is IMethodSymbol { IsGenericMethod: true } method
                        && method.ContainingType.ToDisplayString() == Serializer
                        && method.TypeArguments[0] is { TypeKind: not TypeKind.TypeParameter and not TypeKind.Error } argument
                            ? argument
                            : null)
                .Where(static type => type is not null)
                .Collect();

            var all = services.Combine(contracts).Combine(usages).Combine(context.CompilationProvider);
            context.RegisterSourceOutput(all, static (output, input) =>
            {
                var (((services, contracts), usages), compilation) = input;
                new SerializationEmitter(output, compilation).Emit(
                    services.OfType<INamedTypeSymbol>().Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default).ToImmutableArray(),
                    contracts.Concat(usages.OfType<ITypeSymbol>()).Distinct<ITypeSymbol>(SymbolEqualityComparer.Default).ToImmutableArray());
            });
        }
    }

    internal static class Diagnostics
    {
        private const string Category = "Serialization";

        public static readonly DiagnosticDescriptor UnsupportedType = new(
            "CHRY001", "Unsupported type", "{0} uses {1}, which cannot be serialized: {2}", Category, DiagnosticSeverity.Error, true);
        public static readonly DiagnosticDescriptor UnsupportedMethod = new(
            "CHRY002", "Unsupported operation", "The operation {0} cannot be exposed: {1}", Category, DiagnosticSeverity.Error, true);
        public static readonly DiagnosticDescriptor DuplicateOperation = new(
            "CHRY003", "Duplicate operation name", "The service {0} has more than one operation named '{1}'; rename them with [ChrysalisName]", Category, DiagnosticSeverity.Error, true);
        public static readonly DiagnosticDescriptor NoConstructor = new(
            "CHRY004", "Contract cannot be created", "{0} needs a public parameterless constructor, or one whose parameters match its properties", Category, DiagnosticSeverity.Error, true);
        public static readonly DiagnosticDescriptor DuplicateField = new(
            "CHRY005", "Duplicate member", "{0} has more than one member with the number or name '{1}'", Category, DiagnosticSeverity.Error, true);
        public static readonly DiagnosticDescriptor UnsupportedKey = new(
            "CHRY006", "Unsupported map key", "{0} uses {1}: dictionary keys must be strings", Category, DiagnosticSeverity.Error, true);
    }
}

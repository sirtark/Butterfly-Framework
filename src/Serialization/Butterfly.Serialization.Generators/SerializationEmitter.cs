using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Collections.Immutable;
using System.Text;

namespace Butterfly.Serialization.Generators
{
    // Builds the source of one compilation: descriptions of every reachable type (registered in SerializationRegistry)
    // and, for Chrysalis services, their descriptions and typed clients. Each problem is reported as a diagnostic and
    // leaves out what depends on it, so the generated code always compiles.
    internal sealed class SerializationEmitter
    {
        private const string Ser = "global::Butterfly.Serialization.";
        private const string Chr = "global::Butterfly.Chrysalis.";

        private static readonly SymbolDisplayFormat TypeFormat = SymbolDisplayFormat.FullyQualifiedFormat
            .AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);
        private static readonly SymbolDisplayFormat PlainTypeFormat = SymbolDisplayFormat.FullyQualifiedFormat;

        private readonly SourceProductionContext output;
        private readonly Compilation compilation;
        private readonly string generatedNamespace;
        private readonly string typesClass;

        // Described types, in the order their static fields must be initialized: objects, enums, then lists and maps.
        private readonly Dictionary<ITypeSymbol, TypeEntry> types = new(SymbolEqualityComparer.Default);
        private readonly List<TypeEntry> objects = [];
        private readonly List<TypeEntry> enums = [];
        private readonly List<TypeEntry> collections = [];
        private readonly HashSet<string> schemaNames = [];

        public SerializationEmitter(SourceProductionContext output, Compilation compilation)
        {
            this.output = output;
            this.compilation = compilation;
            var assembly = new string((compilation.AssemblyName ?? "Assembly").Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
            generatedNamespace = "Butterfly.Serialization.Generated." + (char.IsDigit(assembly[0]) ? "_" + assembly : assembly);
            typesClass = "global::" + generatedNamespace + ".GeneratedTypes";
        }

        private sealed class TypeEntry
        {
            public TypeEntry(ITypeSymbol symbol, string field) { Symbol = symbol; Field = field; }
            public ITypeSymbol Symbol { get; }
            public string Field { get; }
            public string Kind { get; set; } = "";
            public string Initialization { get; set; } = "";
            public string Construction { get; set; } = "";
            public bool Failed { get; set; }
        }

        private sealed class OperationModel
        {
            public IMethodSymbol Method = null!;
            public string Name = "";
            public List<(IParameterSymbol Symbol, string Kind, string Name, string Type, bool Optional)> Parameters = [];
            public string? ReturnType;
            public bool ReturnOptional;
            public string ReturnShape = "";   // void, sync, Task, TaskT, ValueTask, ValueTaskT
            public ITypeSymbol? ReturnSymbol;
            public string? HttpMethod;
            public string? HttpRoute;
        }

        public void Emit(ImmutableArray<INamedTypeSymbol> services, ImmutableArray<ITypeSymbol> roots)
        {
            foreach (var root in roots.OrderBy(type => type.ToDisplayString(), StringComparer.Ordinal))
                Resolve(root, root, $"the type {root.ToDisplayString()}", out _);

            var serviceRegistrations = new StringBuilder();
            var describers = new StringBuilder();
            var clients = new StringBuilder();
            var index = 0;
            foreach (var service in services.OrderBy(service => service.ToDisplayString(), StringComparer.Ordinal))
            {
                if (!TryBuildService(service, out var operations))
                    continue;

                var attribute = service.GetAttributes().First(a => a.AttributeClass?.ToDisplayString() == SerializationGenerator.ServiceAttribute);
                var name = Named(attribute, "Name") ?? Positional(attribute) ?? DefaultServiceName(service.Name);
                var ns = Named(attribute, "Namespace") ?? DefaultNamespace(service);
                var contract = service.ToDisplayString(PlainTypeFormat);
                var clientName = ClientName(service);

                serviceRegistrations.Append($"            {Chr}ChrysalisRegistry.RegisterService(typeof({contract}), DescribeService{index}");
                serviceRegistrations.AppendLine(clientName is null ? ");" : $", static invoker => new {Qualified(service, clientName)}(invoker));");
                EmitDescriber(describers, index, contract, name, ns, Named(attribute, "Route"), operations);
                if (clientName is not null)
                    EmitClient(clients, service, contract, clientName, operations);
                index++;
            }

            var described = objects.Concat(enums).Concat(collections).Where(entry => !entry.Failed).ToList();
            if (described.Count == 0 && index == 0)
                return;

            var source = new StringBuilder();
            source.AppendLine("// <auto-generated/> by Butterfly.Serialization.Generators");
            source.AppendLine("#nullable enable");
            source.AppendLine("#pragma warning disable CS0618, CS1591, CA2255, CS8600, CS8601, CS8602, CS8603, CS8604, CS8619, CS8625, CS8631, CS8633");
            source.AppendLine($"namespace {generatedNamespace}");
            source.AppendLine("{");
            source.AppendLine("    [global::System.CodeDom.Compiler.GeneratedCode(\"Butterfly.Serialization.Generators\", \"1.0\")]");
            source.AppendLine("    internal static class GeneratedRegistrations");
            source.AppendLine("    {");
            source.AppendLine("        [global::System.Runtime.CompilerServices.ModuleInitializer]");
            source.AppendLine("        internal static void Register()");
            source.AppendLine("        {");
            foreach (var entry in described)
                source.AppendLine($"            {Ser}SerializationRegistry.Register(typeof({entry.Symbol.ToDisplayString(PlainTypeFormat)}), static () => {typesClass}.{entry.Field});");
            source.Append(serviceRegistrations);
            source.AppendLine("        }");
            source.Append(describers);
            source.AppendLine("    }");
            source.AppendLine();
            EmitTypes(source);
            source.AppendLine("}");
            source.Append(clients);

            output.AddSource("Butterfly.Serialization.g.cs", source.ToString());
        }

        // ---------------------------------------------------------------- Chrysalis services

        private bool TryBuildService(INamedTypeSymbol service, out List<OperationModel> operations)
        {
            operations = [];
            var valid = true;
            var names = new HashSet<string>(StringComparer.Ordinal);

            var methods = new[] { service }.Concat(service.AllInterfaces)
                .SelectMany(type => type.GetMembers().OfType<IMethodSymbol>())
                .Where(method => method.MethodKind == MethodKind.Ordinary && !method.IsStatic && !HasAttribute(method, "ChrysalisIgnoreAttribute"));

            foreach (var method in methods)
            {
                var operation = new OperationModel { Method = method, Name = Renamed(method) ?? method.Name };
                if (!names.Add(operation.Name))
                {
                    Report(Diagnostics.DuplicateOperation, method, service.Name, operation.Name);
                    valid = false;
                    continue;
                }
                if (method.IsGenericMethod)
                {
                    Report(Diagnostics.UnsupportedMethod, method, Describe(method), "generic methods have no wire representation");
                    valid = false;
                    continue;
                }

                foreach (var parameter in method.Parameters)
                {
                    if (parameter.RefKind != RefKind.None)
                    {
                        Report(Diagnostics.UnsupportedMethod, parameter, Describe(method), $"the parameter '{parameter.Name}' is ref, out or in");
                        valid = false;
                        continue;
                    }

                    var full = parameter.Type.ToDisplayString(PlainTypeFormat);
                    if (full == "global::System.Threading.CancellationToken")
                        operation.Parameters.Add((parameter, "token", parameter.Name, full, false));
                    else if (full == "global::Butterfly.Chrysalis.ChrysalisCallContext")
                        operation.Parameters.Add((parameter, "context", parameter.Name, full, false));
                    else if (Resolve(parameter.Type, parameter, $"the parameter '{parameter.Name}' of {Describe(method)}", out var optional) is { } type)
                        operation.Parameters.Add((parameter, "value", Renamed(parameter) ?? parameter.Name, type, optional));
                    else
                        valid = false;
                }

                if (!TryResolveReturn(method, operation))
                    valid = false;

                var http = method.GetAttributes().FirstOrDefault(a => InheritsFrom(a.AttributeClass, "Butterfly.Chrysalis.HttpMethodAttribute"));
                if (http is not null)
                {
                    operation.HttpMethod = http.AttributeClass!.Name switch
                    {
                        "HttpGetAttribute" => "GET",
                        "HttpPutAttribute" => "PUT",
                        "HttpPatchAttribute" => "PATCH",
                        "HttpDeleteAttribute" => "DELETE",
                        _ => "POST"
                    };
                    operation.HttpRoute = Positional(http);
                }
                operations.Add(operation);
            }
            return valid;
        }

        private bool TryResolveReturn(IMethodSymbol method, OperationModel operation)
        {
            var returnType = method.ReturnType;
            ITypeSymbol value;
            switch (returnType.OriginalDefinition.ToDisplayString(PlainTypeFormat))
            {
                case "void":
                    operation.ReturnShape = "void";
                    return true;
                case "global::System.Threading.Tasks.Task":
                    operation.ReturnShape = "Task";
                    return true;
                case "global::System.Threading.Tasks.ValueTask":
                    operation.ReturnShape = "ValueTask";
                    return true;
                case "global::System.Threading.Tasks.Task<TResult>":
                    operation.ReturnShape = "TaskT";
                    value = ((INamedTypeSymbol)returnType).TypeArguments[0];
                    break;
                case "global::System.Threading.Tasks.ValueTask<TResult>":
                    operation.ReturnShape = "ValueTaskT";
                    value = ((INamedTypeSymbol)returnType).TypeArguments[0];
                    break;
                case "global::System.Collections.Generic.IAsyncEnumerable<T>":
                    Report(Diagnostics.UnsupportedMethod, method, Describe(method), "streaming (IAsyncEnumerable) is not supported");
                    return false;
                default:
                    operation.ReturnShape = "sync";
                    value = returnType;
                    break;
            }

            operation.ReturnSymbol = value;
            operation.ReturnType = Resolve(value, method, $"the result of {Describe(method)}", out operation.ReturnOptional);
            return operation.ReturnType is not null;
        }

        private static void EmitDescriber(StringBuilder source, int index, string contract, string name, string ns, string? route, List<OperationModel> operations)
        {
            source.AppendLine();
            source.AppendLine($"        private static {Chr}ChrysalisService DescribeService{index}() => new {Chr}ChrysalisService(");
            source.AppendLine($"            {Literal(name)}, {Literal(ns)}, typeof({contract}), {Literal(route)},");
            source.AppendLine($"            new {Chr}ChrysalisOperation[]");
            source.AppendLine("            {");
            for (var i = 0; i < operations.Count; i++)
            {
                var operation = operations[i];
                var parameters = string.Join(", ", operation.Parameters.Where(p => p.Kind == "value")
                    .Select(p => $"new {Chr}ChrysalisParameter({Literal(p.Name)}, {p.Type}, {Bool(p.Optional)})"));
                var http = operation.HttpMethod is null ? "null" : $"new {Chr}ChrysalisHttpBinding({Literal(operation.HttpMethod)}, {Literal(operation.HttpRoute)})";
                source.AppendLine($"                new {Chr}ChrysalisOperation({Literal(operation.Name)}, new {Chr}ChrysalisParameter[] {{ {parameters} }},");
                source.AppendLine($"                    {operation.ReturnType ?? "null"}, {Bool(operation.ReturnOptional)}, Invoke{index}_{i}, {http}),");
            }
            source.AppendLine("            });");

            for (var i = 0; i < operations.Count; i++)
            {
                var operation = operations[i];
                var call = $"(({contract})service).{Escape(operation.Method.Name)}({Arguments(operation)})";
                var signature = $"(object service, object?[] arguments, {Chr}ChrysalisCallContext context)";
                source.AppendLine();
                switch (operation.ReturnShape)
                {
                    case "void":
                        source.AppendLine($"        private static global::System.Threading.Tasks.ValueTask<object?> Invoke{index}_{i}{signature}");
                        source.AppendLine("        {");
                        source.AppendLine($"            {call};");
                        source.AppendLine("            return default;");
                        source.AppendLine("        }");
                        break;
                    case "sync":
                        source.AppendLine($"        private static global::System.Threading.Tasks.ValueTask<object?> Invoke{index}_{i}{signature} =>");
                        source.AppendLine($"            new global::System.Threading.Tasks.ValueTask<object?>({call});");
                        break;
                    case "Task":
                    case "ValueTask":
                        source.AppendLine($"        private static async global::System.Threading.Tasks.ValueTask<object?> Invoke{index}_{i}{signature}");
                        source.AppendLine("        {");
                        source.AppendLine($"            await {call}.ConfigureAwait(false);");
                        source.AppendLine("            return null;");
                        source.AppendLine("        }");
                        break;
                    default:
                        source.AppendLine($"        private static async global::System.Threading.Tasks.ValueTask<object?> Invoke{index}_{i}{signature} =>");
                        source.AppendLine($"            await {call}.ConfigureAwait(false);");
                        break;
                }
            }
        }

        private static string Arguments(OperationModel operation)
        {
            var arguments = new List<string>();
            var slot = 0;
            foreach (var (symbol, kind, _, _, _) in operation.Parameters)
            {
                arguments.Add(kind switch
                {
                    "token" => "context.CancellationToken",
                    "context" => "context",
                    _ => $"({symbol.Type.ToDisplayString(TypeFormat)})arguments[{slot++}]!"
                });
            }
            return string.Join(", ", arguments);
        }

        private static void EmitClient(StringBuilder source, INamedTypeSymbol service, string contract, string clientName, List<OperationModel> operations)
        {
            var accessibility = service.DeclaredAccessibility == Accessibility.Public ? "public" : "internal";
            var ns = service.ContainingNamespace.IsGlobalNamespace ? null : service.ContainingNamespace.ToDisplayString();

            source.AppendLine();
            if (ns is not null)
                source.AppendLine($"namespace {ns}").AppendLine("{");
            source.AppendLine($"    /// <summary>Client of {Xml(service.Name)} that sends every call through an <see cref=\"{Chr}IChrysalisInvoker\"/>.</summary>");
            source.AppendLine("    [global::System.CodeDom.Compiler.GeneratedCode(\"Butterfly.Serialization.Generators\", \"1.0\")]");
            source.AppendLine($"    {accessibility} sealed partial class {clientName} : {contract}");
            source.AppendLine("    {");
            source.AppendLine($"        private readonly {Chr}IChrysalisInvoker invoker;");
            source.AppendLine($"        private readonly {Chr}ChrysalisService service;");
            source.AppendLine();
            source.AppendLine($"        public {clientName}({Chr}IChrysalisInvoker invoker)");
            source.AppendLine("        {");
            source.AppendLine("            this.invoker = invoker ?? throw new global::System.ArgumentNullException(nameof(invoker));");
            source.AppendLine($"            service = {Chr}ChrysalisRegistry.GetService(typeof({contract}));");
            source.AppendLine("        }");

            for (var i = 0; i < operations.Count; i++)
            {
                var operation = operations[i];
                var method = operation.Method;
                var parameters = string.Join(", ", method.Parameters.Select(p => $"{p.Type.ToDisplayString(TypeFormat)} {Escape(p.Name)}"));
                var values = string.Join(", ", operation.Parameters.Where(p => p.Kind == "value").Select(p => $"(object?){Escape(p.Symbol.Name)}"));
                var token = operation.Parameters.FirstOrDefault(p => p.Kind == "token").Symbol is { } t ? Escape(t.Name) : "global::System.Threading.CancellationToken.None";
                var invoke = $"invoker.InvokeAsync(service.Operations[{i}], new object?[] {{ {values} }}, {token})";
                var owner = method.ContainingType.ToDisplayString(PlainTypeFormat);
                var returns = method.ReturnType.ToDisplayString(TypeFormat);
                var result = operation.ReturnSymbol?.ToDisplayString(TypeFormat);

                source.AppendLine();
                switch (operation.ReturnShape)
                {
                    case "void":
                        source.AppendLine($"        void {owner}.{Escape(method.Name)}({parameters}) => {invoke}.AsTask().GetAwaiter().GetResult();");
                        break;
                    case "sync":
                        source.AppendLine($"        {returns} {owner}.{Escape(method.Name)}({parameters}) => ({result}){invoke}.AsTask().GetAwaiter().GetResult()!;");
                        break;
                    case "Task":
                    case "ValueTask":
                        source.AppendLine($"        async {returns} {owner}.{Escape(method.Name)}({parameters}) => await {invoke}.ConfigureAwait(false);");
                        break;
                    default:
                        source.AppendLine($"        async {returns} {owner}.{Escape(method.Name)}({parameters}) => ({result})(await {invoke}.ConfigureAwait(false))!;");
                        break;
                }
            }

            source.AppendLine("    }");
            if (ns is not null)
                source.AppendLine("}");
        }

        // ---------------------------------------------------------------- types

        // The expression that yields the SerializableType of a CLR type, or null (after reporting why) when it is not supported.
        private string? Resolve(ITypeSymbol type, ISymbol location, string usage, out bool optional)
        {
            optional = false;
            // A type the compiler could not bind: it reports the error itself.
            if (ContainsErrors(type))
                return null;
            if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
            {
                optional = true;
                type = nullable.TypeArguments[0];
            }
            else if (type.IsReferenceType && type.NullableAnnotation == NullableAnnotation.Annotated)
            {
                optional = true;
            }

            switch (type.SpecialType)
            {
                case SpecialType.System_Boolean: return Ser + "SerializableType.Boolean";
                case SpecialType.System_Int32: return Ser + "SerializableType.Int32";
                case SpecialType.System_Int64: return Ser + "SerializableType.Int64";
                case SpecialType.System_Double: return Ser + "SerializableType.Double";
                case SpecialType.System_Decimal: return Ser + "SerializableType.Decimal";
                case SpecialType.System_String: return Ser + "SerializableType.String";
                case SpecialType.System_DateTime: return Ser + "SerializableType.DateTime";
                case SpecialType.System_Object:
                    optional = true;
                    return Ser + "DynamicType.Object";
            }

            switch (type.ToDisplayString(PlainTypeFormat))
            {
                case "byte[]": return Ser + "SerializableType.Bytes";
                case "global::System.DateTimeOffset": return Ser + "SerializableType.Timestamp";
                case "global::System.Guid": return Ser + "SerializableType.Guid";
                case "global::System.TimeSpan": return Ser + "SerializableType.Duration";
                case "global::Butterfly.Serialization.SerializationValue": optional = true; return Ser + "DynamicType.Value";
                case "global::System.Text.Json.Nodes.JsonNode": optional = true; return Ser + "DynamicType.JsonNode";
                case "global::System.Text.Json.Nodes.JsonObject": optional = true; return Ser + "DynamicType.JsonObject";
                case "global::System.Text.Json.Nodes.JsonArray": optional = true; return Ser + "DynamicType.JsonArray";
                case "global::System.Text.Json.Nodes.JsonValue": optional = true; return Ser + "DynamicType.JsonValue";
                case "global::System.Text.Json.JsonElement": return Ser + "DynamicType.JsonElement";
            }

            if (types.TryGetValue(type, out var known))
                return known.Failed ? null : $"{typesClass}.{known.Field}";

            if (type.TypeKind == TypeKind.Enum)
                return DescribeEnum((INamedTypeSymbol)type);

            if (MapOf(type) is { } map)
                return DescribeMap(type, map.Key, map.Value, location, usage);

            if (ElementOf(type) is { } element)
                return DescribeList(type, element, location, usage);

            if (type is INamedTypeSymbol { TypeKind: TypeKind.Class or TypeKind.Struct } named && !IsSystemType(named) && !named.IsAbstract)
            {
                if (!IsAccessible(named))
                {
                    Report(Diagnostics.UnsupportedType, location, usage, type.ToDisplayString(), "it must be public or internal (not nested in a private type)");
                    return null;
                }
                return DescribeObject(named, location);
            }

            Report(Diagnostics.UnsupportedType, location, usage, type.ToDisplayString(),
                "use bool, int, long, double, decimal, string, byte[], DateTime(Offset), Guid, enums, arrays/lists, string-keyed dictionaries, object/JsonNode, or classes and records of those");
            return null;
        }

        private string DescribeEnum(INamedTypeSymbol type)
        {
            var entry = Add(type, enums, "EnumType");
            var display = type.ToDisplayString(PlainTypeFormat);
            var members = type.GetMembers().OfType<IFieldSymbol>().Where(field => field.HasConstantValue)
                .Select(field => $"new {Ser}EnumMember({Literal(AttributeText(field, "EnumNameAttribute") ?? field.Name)}, {System.Convert.ToInt64(field.ConstantValue)}L)");
            entry.Construction = $"new {Ser}EnumType({Literal(SchemaName(type))}, typeof({display}), new {Ser}EnumMember[] {{ {string.Join(", ", members)} }}, " +
                $"static value => (long)({display})value, static number => ({display})number)";
            return $"{typesClass}.{entry.Field}";
        }

        private string? DescribeList(ITypeSymbol type, ITypeSymbol element, ISymbol location, string usage)
        {
            if (ElementOf(element) is not null || MapOf(element) is not null)
            {
                Report(Diagnostics.UnsupportedType, location, usage, type.ToDisplayString(), "nested collections cannot be represented in every format; wrap the inner one in a class");
                return null;
            }

            var elementType = Resolve(element, location, usage, out var elementOptional);
            if (elementType is null)
                return null;
            if (elementOptional && element.IsValueType)
            {
                Report(Diagnostics.UnsupportedType, location, usage, type.ToDisplayString(), "list elements cannot be nullable value types");
                return null;
            }

            var entry = Add(type, collections, "ListType");
            var elementDisplay = element.ToDisplayString(TypeFormat);
            var select = $"global::System.Linq.Enumerable.Select(items, static item => ({elementDisplay})item!)";
            var create = type is IArrayTypeSymbol
                ? $"global::System.Linq.Enumerable.ToArray({select})"
                : $"new global::System.Collections.Generic.List<{elementDisplay}>({select})";
            entry.Construction = $"new {Ser}ListType({elementType}, typeof({type.ToDisplayString(PlainTypeFormat)}), static items => {create})";
            return $"{typesClass}.{entry.Field}";
        }

        private string? DescribeMap(ITypeSymbol type, ITypeSymbol key, ITypeSymbol value, ISymbol location, string usage)
        {
            if (key.SpecialType != SpecialType.System_String)
            {
                Report(Diagnostics.UnsupportedKey, location, usage, type.ToDisplayString());
                return null;
            }
            if (ElementOf(value) is not null || MapOf(value) is not null)
            {
                Report(Diagnostics.UnsupportedType, location, usage, type.ToDisplayString(), "nested collections cannot be represented in every format; wrap the inner one in a class");
                return null;
            }

            var valueType = Resolve(value, location, usage, out var valueOptional);
            if (valueType is null)
                return null;

            var entry = Add(type, collections, "MapType");
            var valueDisplay = value.ToDisplayString(TypeFormat);
            var create = $"static entries => {{ var map = new global::System.Collections.Generic.Dictionary<string, {valueDisplay}>(entries.Count); foreach (var entry in entries) map[entry.Key] = ({valueDisplay})entry.Value!; return map; }}";
            var enumerate = $"static map => global::System.Linq.Enumerable.Select((global::System.Collections.Generic.IEnumerable<global::System.Collections.Generic.KeyValuePair<string, {valueDisplay}>>)map, " +
                $"static pair => new global::System.Collections.Generic.KeyValuePair<string, object?>(pair.Key, pair.Value))";
            entry.Construction = $"new {Ser}MapType({valueType}, typeof({type.ToDisplayString(PlainTypeFormat)}), {create}, {enumerate}, {Bool(valueOptional)})";
            return $"{typesClass}.{entry.Field}";
        }

        private string? DescribeObject(INamedTypeSymbol type, ISymbol location)
        {
            // Registered before its members are resolved, so recursive types refer to themselves.
            var entry = Add(type, objects, "ObjectType");
            var display = type.ToDisplayString(PlainTypeFormat);
            var contract = type.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == SerializationGenerator.ContractAttribute);
            // SerializationMode is a byte enum: its constant arrives as a byte, not an int.
            var optIn = contract?.NamedArguments.FirstOrDefault(argument => argument.Key == "Mode").Value.Value is { } mode && System.Convert.ToInt32(mode) == 1;
            var schemaName = (contract is null ? null : Named(contract, "Name")) ?? SchemaName(type);
            entry.Construction = $"new {Ser}ObjectType({Literal(schemaName)}, {Literal(DefaultNamespace(type))}, typeof({display}))";

            var properties = PropertiesOf(type);
            var constructor = ChooseConstructor(type, properties);
            if (constructor is null)
            {
                Report(Diagnostics.NoConstructor, location, type.ToDisplayString());
                entry.Failed = true;
                return null;
            }

            var constructorProperties = new HashSet<IPropertySymbol>(
                constructor.Parameters.Select(p => properties.First(property => Matches(property, p))), SymbolEqualityComparer.Default);
            var members = properties
                .Where(property => constructorProperties.Contains(property) || IsSettable(property))
                .Where(property => IsIncluded(property, optIn))
                .ToList();

            // Numbers: explicit ones first, then the free numbers in declaration order.
            var used = new HashSet<int>();
            foreach (var member in members)
            {
                if (SerializeNumber(member) is int number && !used.Add(number))
                {
                    Report(Diagnostics.DuplicateField, location, type.ToDisplayString(), number.ToString());
                    entry.Failed = true;
                    return null;
                }
            }

            var next = 1;
            var fields = new List<string>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var valid = true;
            foreach (var member in members)
            {
                var number = SerializeNumber(member);
                if (number is null)
                {
                    while (used.Contains(next) || next is >= 19000 and <= 19999)
                        next++;
                    number = next;
                    used.Add(next);
                }

                var explicitName = SerializeName(member);
                var name = explicitName ?? member.Name;
                if (!names.Add(name))
                {
                    Report(Diagnostics.DuplicateField, member, type.ToDisplayString(), name);
                    valid = false;
                    continue;
                }

                var memberType = Resolve(member.Type, member, $"the property {type.Name}.{member.Name}", out var optional);
                if (memberType is null)
                {
                    valid = false;
                    continue;
                }

                var includedIn = Profiles(member, "SerializeAttribute");
                var excludedFrom = Profiles(member, "DontSerializeAttribute");
                fields.Add($"new {Ser}SerializableMember({Literal(name)}, {number}, {memberType}, {Bool(optional)}, static instance => (({display})instance).{Escape(member.Name)}, " +
                    $"hasExplicitName: {Bool(explicitName is not null)}, includedIn: {StringArray(includedIn)}, excludedFrom: {StringArray(excludedFrom)})");
            }
            if (!valid)
            {
                entry.Failed = true;
                return null;
            }

            var valueOf = new Dictionary<IPropertySymbol, string>(SymbolEqualityComparer.Default);
            for (var i = 0; i < members.Count; i++)
                valueOf[members[i]] = $"({members[i].Type.ToDisplayString(TypeFormat)})values[{i}]!";

            // Constructor parameters of members left out of the contract get their default.
            var arguments = string.Join(", ", constructor.Parameters.Select(p =>
            {
                var property = properties.First(candidate => Matches(candidate, p));
                return valueOf.TryGetValue(property, out var value) ? value : $"default({property.Type.ToDisplayString(TypeFormat)})!";
            }));
            var initializers = members.Where(member => !constructorProperties.Contains(member)).Select(member => $"{Escape(member.Name)} = {valueOf[member]}").ToList();
            var create = $"new {display}({arguments})" + (initializers.Count > 0 ? " { " + string.Join(", ", initializers) + " }" : "");

            entry.Initialization = $"{entry.Field}.Initialize(new {Ser}SerializableMember[] {{ {string.Join(", ", fields)} }}, static values => {create});";
            return $"{typesClass}.{entry.Field}";
        }

        // OptOut: every property except [DontSerialize] without profiles. OptIn: only [Serialize].
        private static bool IsIncluded(IPropertySymbol property, bool optIn)
        {
            var dont = property.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == "DontSerializeAttribute");
            if (dont is not null && ProfilesOf(dont).Count == 0)
                return false;
            return !optIn || HasAttribute(property, "SerializeAttribute");
        }

        private void EmitTypes(StringBuilder source)
        {
            source.AppendLine("    [global::System.CodeDom.Compiler.GeneratedCode(\"Butterfly.Serialization.Generators\", \"1.0\")]");
            source.AppendLine("    internal static class GeneratedTypes");
            source.AppendLine("    {");
            // Static fields initialize in textual order: collections come last because they refer to their element types.
            foreach (var entry in objects.Concat(enums).Concat(collections).Where(entry => !entry.Failed))
                source.AppendLine($"        internal static readonly {Ser}{entry.Kind} {entry.Field} = {entry.Construction};");
            source.AppendLine();
            source.AppendLine("        static GeneratedTypes()");
            source.AppendLine("        {");
            foreach (var entry in objects.Where(entry => !entry.Failed && entry.Initialization.Length > 0))
                source.AppendLine($"            {entry.Initialization}");
            source.AppendLine("        }");
            source.AppendLine("    }");
        }

        private TypeEntry Add(ITypeSymbol type, List<TypeEntry> group, string kind)
        {
            var entry = new TypeEntry(type, "Type" + types.Count) { Kind = kind };
            types[type] = entry;
            group.Add(entry);
            return entry;
        }

        // ---------------------------------------------------------------- symbols

        private static bool ContainsErrors(ITypeSymbol type) => type switch
        {
            { TypeKind: TypeKind.Error } => true,
            IArrayTypeSymbol array => ContainsErrors(array.ElementType),
            INamedTypeSymbol named => named.TypeArguments.Any(ContainsErrors),
            _ => false
        };

        private static ITypeSymbol? ElementOf(ITypeSymbol type)
        {
            if (type is IArrayTypeSymbol array)
                return array.Rank == 1 && array.ElementType.SpecialType != SpecialType.System_Byte ? array.ElementType : null;

            if (type is INamedTypeSymbol { IsGenericType: true } named && named.TypeArguments.Length == 1)
            {
                switch (named.OriginalDefinition.ToDisplayString(PlainTypeFormat))
                {
                    case "global::System.Collections.Generic.List<T>":
                    case "global::System.Collections.Generic.IList<T>":
                    case "global::System.Collections.Generic.ICollection<T>":
                    case "global::System.Collections.Generic.IReadOnlyList<T>":
                    case "global::System.Collections.Generic.IReadOnlyCollection<T>":
                    case "global::System.Collections.Generic.IEnumerable<T>":
                        return named.TypeArguments[0];
                }
            }
            return null;
        }

        private static (ITypeSymbol Key, ITypeSymbol Value)? MapOf(ITypeSymbol type)
        {
            if (type is INamedTypeSymbol { IsGenericType: true } named && named.TypeArguments.Length == 2)
            {
                switch (named.OriginalDefinition.ToDisplayString(PlainTypeFormat))
                {
                    case "global::System.Collections.Generic.Dictionary<TKey, TValue>":
                    case "global::System.Collections.Generic.IDictionary<TKey, TValue>":
                    case "global::System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>":
                        return (named.TypeArguments[0], named.TypeArguments[1]);
                }
            }
            return null;
        }

        // Public readable instance properties, base classes first; a property redeclared in a derived class replaces the base one.
        private static List<IPropertySymbol> PropertiesOf(INamedTypeSymbol type)
        {
            var chain = new List<INamedTypeSymbol>();
            for (var current = type; current is not null && current.SpecialType != SpecialType.System_Object && current.SpecialType != SpecialType.System_ValueType; current = current.BaseType)
                chain.Insert(0, current);

            var properties = new List<IPropertySymbol>();
            foreach (var current in chain)
            {
                foreach (var property in current.GetMembers().OfType<IPropertySymbol>())
                {
                    if (property.IsStatic || property.IsIndexer || property.DeclaredAccessibility != Accessibility.Public
                        || property.GetMethod is null || property.GetMethod.DeclaredAccessibility != Accessibility.Public
                        || property.Name == "EqualityContract")
                        continue;

                    properties.RemoveAll(existing => existing.Name == property.Name);
                    properties.Add(property);
                }
            }
            return properties;
        }

        // The public constructor with the most parameters that all match properties (records), or the parameterless one.
        private static IMethodSymbol? ChooseConstructor(INamedTypeSymbol type, List<IPropertySymbol> properties) =>
            type.InstanceConstructors
                .Where(constructor => constructor.DeclaredAccessibility == Accessibility.Public)
                .Where(constructor => constructor.Parameters.All(parameter => properties.Any(property => Matches(property, parameter))))
                .Where(constructor => !(constructor.Parameters.Length == 1 && SymbolEqualityComparer.Default.Equals(constructor.Parameters[0].Type, type)))
                .OrderByDescending(constructor => constructor.Parameters.Length)
                .FirstOrDefault();

        private static bool Matches(IPropertySymbol property, IParameterSymbol parameter) =>
            string.Equals(property.Name, parameter.Name, StringComparison.OrdinalIgnoreCase)
            && SymbolEqualityComparer.Default.Equals(property.Type, parameter.Type);

        private static bool IsSettable(IPropertySymbol property) => property.SetMethod is { DeclaredAccessibility: Accessibility.Public };

        private static AttributeData? Attribute(ISymbol symbol, string name) => symbol.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == name);

        private static int? SerializeNumber(IPropertySymbol property) =>
            Attribute(property, "SerializeAttribute")?.NamedArguments.FirstOrDefault(argument => argument.Key == "Number").Value.Value is int number && number > 0 ? number : null;

        private static string? SerializeName(IPropertySymbol property) =>
            Attribute(property, "SerializeAttribute") is { } attribute ? Named(attribute, "Name") ?? Positional(attribute) : null;

        private static List<string> Profiles(IPropertySymbol property, string attribute) =>
            Attribute(property, attribute) is { } data ? ProfilesOf(data) : [];

        // Profiles = new[] { ... } (named) or params string[] (constructor).
        private static List<string> ProfilesOf(AttributeData attribute)
        {
            var named = attribute.NamedArguments.FirstOrDefault(argument => argument.Key == "Profiles").Value;
            var constant = named.Kind == TypedConstantKind.Array ? named
                : attribute.ConstructorArguments.FirstOrDefault(argument => argument.Kind == TypedConstantKind.Array);
            return constant.Kind == TypedConstantKind.Array && !constant.IsNull
                ? constant.Values.Select(value => value.Value as string).Where(value => value is not null).Select(value => value!).ToList()
                : [];
        }

        private static string? Renamed(ISymbol symbol) => AttributeText(symbol, "ChrysalisNameAttribute");

        private static string? AttributeText(ISymbol symbol, string attribute) =>
            Attribute(symbol, attribute) is { } data ? Positional(data) : null;

        private static bool HasAttribute(ISymbol symbol, string name) => symbol.GetAttributes().Any(a => a.AttributeClass?.Name == name);

        private static bool InheritsFrom(INamedTypeSymbol? type, string baseName)
        {
            for (var current = type; current is not null; current = current.BaseType)
            {
                if (current.ToDisplayString() == baseName)
                    return true;
            }
            return false;
        }

        private static bool IsSystemType(INamedTypeSymbol type)
        {
            var ns = type.ContainingNamespace?.ToDisplayString() ?? "";
            return ns == "System" || ns.StartsWith("System.", StringComparison.Ordinal) || ns.StartsWith("Microsoft.", StringComparison.Ordinal);
        }

        private bool IsAccessible(INamedTypeSymbol type)
        {
            for (ISymbol? current = type; current is INamedTypeSymbol named; current = named.ContainingType)
            {
                if (named.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal))
                    return false;
                if (named.DeclaredAccessibility == Accessibility.Internal && !SymbolEqualityComparer.Default.Equals(named.ContainingAssembly, compilation.Assembly))
                    return false;
            }
            return true;
        }

        private string SchemaName(INamedTypeSymbol type)
        {
            var name = type.IsGenericType
                ? type.Name + "_" + string.Join("_", type.TypeArguments.Select(argument => argument.Name))
                : type.Name;
            if (schemaNames.Add(name))
                return name;

            // Two contracts with the same name in different namespaces.
            var qualified = (type.ContainingNamespace.ToDisplayString() + "." + name).Replace('.', '_');
            schemaNames.Add(qualified);
            return qualified;
        }

        private static string DefaultServiceName(string interfaceName) =>
            interfaceName.Length > 1 && interfaceName[0] == 'I' && char.IsUpper(interfaceName[1]) ? interfaceName.Substring(1) : interfaceName;

        private static string DefaultNamespace(ITypeSymbol type) =>
            type.ContainingNamespace is null || type.ContainingNamespace.IsGlobalNamespace ? "butterfly" : type.ContainingNamespace.ToDisplayString().ToLowerInvariant();

        // Clients are generated next to top-level interfaces only.
        private static string? ClientName(INamedTypeSymbol service) =>
            service.ContainingType is null ? DefaultServiceName(service.Name) + "Client" : null;

        private static string Qualified(INamedTypeSymbol service, string name) =>
            service.ContainingNamespace.IsGlobalNamespace ? "global::" + name : $"global::{service.ContainingNamespace.ToDisplayString()}.{name}";

        private static string? Named(AttributeData attribute, string name) =>
            attribute.NamedArguments.FirstOrDefault(argument => argument.Key == name).Value.Value as string;

        private static string? Positional(AttributeData attribute) =>
            attribute.ConstructorArguments.Length > 0 && attribute.ConstructorArguments[0].Kind != TypedConstantKind.Array ? attribute.ConstructorArguments[0].Value as string : null;

        private static string Describe(IMethodSymbol method) => $"{method.ContainingType.Name}.{method.Name}";

        private static string Literal(string? value) => value is null ? "null" : SymbolDisplay.FormatLiteral(value, quote: true);
        private static string Bool(bool value) => value ? "true" : "false";
        private static string StringArray(List<string> values) => values.Count == 0 ? "null" : "new string[] { " + string.Join(", ", values.Select(value => Literal(value))) + " }";
        private static string Escape(string name) => SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ? "@" + name : name;
        private static string Xml(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

        private void Report(DiagnosticDescriptor descriptor, ISymbol symbol, params object[] arguments) =>
            output.ReportDiagnostic(Diagnostic.Create(descriptor, symbol.Locations.FirstOrDefault(), arguments));
    }
}

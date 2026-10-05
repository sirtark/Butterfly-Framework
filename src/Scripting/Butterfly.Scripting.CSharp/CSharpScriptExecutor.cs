using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Scripting;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;

namespace Butterfly.Scripting.CSharp
{
    public sealed class CSharpScriptExecutor : IScriptExecutor
    {
        static readonly CSharpParseOptions ScriptParseOptions = CSharpParseOptions.Default
            .WithKind(SourceCodeKind.Script)
            .WithLanguageVersion(LanguageVersion.Latest);

        private readonly CSharpScriptConfig _config;
        private readonly ScriptOptions _options;
        private readonly CSharpCompiledScriptCache? _cache;

        public CSharpScriptExecutor() : this(CSharpScriptConfig.Default) { }

        public CSharpScriptExecutor(CSharpScriptConfig config)
        {
            ArgumentNullException.ThrowIfNull(config, nameof(config));

            if (config.Mode == CSharpScriptMode.Context)
            {
                if (config.ContextType is null)
                    throw new ArgumentException("Context mode requires a context type.", nameof(config));
                if (!config.ContextType.IsVisible || config.ContextType.GetConstructor(Type.EmptyTypes) is null)
                    throw new ArgumentException($"The context type {config.ContextType} must be public and have a public parameterless constructor.", nameof(config));
            }
            if (config.Flags.HasFlag(CSharpScriptFlags.DependencyInjection) && config.Mode != CSharpScriptMode.Delegate)
                throw new NotSupportedException($"Dependency injection is only supported in {CSharpScriptMode.Delegate} mode.");

            _config = config;
            _options = config.ToScriptOptions();
            _cache = config.CompilationCacheSize > 0 ? new CSharpCompiledScriptCache(config.CompilationCacheSize) : null;
        }

        public ScriptingLanguage Lenguage => ScriptingLanguage.CSharp;

        public CSharpScriptMode Mode => _config.Mode;

        bool DependencyInjectionEnabled => _config.Flags.HasFlag(CSharpScriptFlags.DependencyInjection);

        Type GlobalsType => _config.Mode switch
        {
            CSharpScriptMode.Dynamic => typeof(CSharpDynamicScriptGlobals),
            CSharpScriptMode.Context => _config.ContextType!,
            _ => typeof(CSharpScriptGlobals)
        };

        public ValueTask<ScriptValidationResult> ValidateAsync(string scriptContent, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Validate(scriptContent, cancellationToken));
        }

        ScriptValidationResult Validate(string scriptContent, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(scriptContent))
                return ScriptValidationResult.Failure("The script is empty.");

            if (_config.Mode == CSharpScriptMode.Expression)
            {
                var expression = SyntaxFactory.ParseExpression(scriptContent, options: ScriptParseOptions);
                return expression.ContainsDiagnostics
                    ? ScriptValidationResult.Failure(["Expression mode only accepts a single C# expression.", .. Errors(expression.GetDiagnostics())])
                    : ScriptValidationResult.Success;
            }

            var tree = CSharpSyntaxTree.ParseText(scriptContent, ScriptParseOptions, cancellationToken: cancellationToken);
            var errors = Errors(tree.GetDiagnostics(cancellationToken));
            if (errors.Length > 0)
                return ScriptValidationResult.Failure(errors);
            if (_config.Mode == CSharpScriptMode.Delegate && !EndsWithTypedLambda(tree.GetCompilationUnitRoot(cancellationToken)))
                return ScriptValidationResult.Failure("Delegate mode requires the script to end with a lambda whose parameters are explicitly typed, without a trailing semicolon.");
            return ScriptValidationResult.Success;
        }

        public async Task<object?> ExecuteAsync(string scriptContent, IReadOnlyDictionary<string, object> parameters, IServiceProvider? services = null, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(parameters, nameof(parameters));

            var script = GetCompiledScript(scriptContent, cancellationToken);
            object? globals = _config.Mode switch
            {
                CSharpScriptMode.Expression or CSharpScriptMode.Parameters => new CSharpScriptGlobals(parameters),
                CSharpScriptMode.Dynamic => new CSharpDynamicScriptGlobals(parameters),
                CSharpScriptMode.Context => CreateContext(parameters),
                _ => null
            };
            var state = await script.RunAsync(globals, cancellationToken).ConfigureAwait(false);

            if (_config.Mode != CSharpScriptMode.Delegate)
                return state.ReturnValue;
            if (state.ReturnValue is not Delegate @delegate)
                throw new ScriptException(ScriptingLanguage.CSharp, "The script did not produce a delegate.");
            return await InvokeDelegateAsync(@delegate, parameters, services, cancellationToken).ConfigureAwait(false);
        }

        Script GetCompiledScript(string scriptContent, CancellationToken cancellationToken)
        {
            if (_cache is not null && _cache.TryGet(scriptContent, out var cached))
                return cached;

            Script script = _config.Mode == CSharpScriptMode.Delegate
                ? CSharpScript.Create<Delegate>(scriptContent, _options)
                : CSharpScript.Create<object>(scriptContent, _options, GlobalsType);
            var errors = Errors(script.Compile(cancellationToken));
            if (errors.Length > 0)
                throw new ScriptCompilationException(ScriptingLanguage.CSharp, errors);

            _cache?.Add(scriptContent, script);
            return script;
        }

        object CreateContext(IReadOnlyDictionary<string, object> parameters)
        {
            var contextType = _config.ContextType!;
            var context = Activator.CreateInstance(contextType)!;
            foreach (var (name, value) in parameters)
            {
                var property = contextType.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (property?.SetMethod is { IsPublic: true })
                    property.SetValue(context, ConvertArgument(value, property.PropertyType, name));
            }
            return context;
        }

        async Task<object?> InvokeDelegateAsync(Delegate @delegate, IReadOnlyDictionary<string, object> parameters, IServiceProvider? services, CancellationToken cancellationToken)
        {
            var arguments = @delegate.Method.GetParameters()
                .Select(parameter => BindArgument(parameter, parameters, services, cancellationToken))
                .ToArray();

            object? result;
            try
            {
                result = @delegate.DynamicInvoke(arguments);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                ExceptionDispatchInfo.Throw(ex.InnerException);
                throw;
            }
            return await AwaitResultAsync(result, @delegate.Method.ReturnType).ConfigureAwait(false);
        }

        object? BindArgument(ParameterInfo parameter, IReadOnlyDictionary<string, object> parameters, IServiceProvider? services, CancellationToken cancellationToken)
        {
            var name = parameter.Name ?? string.Empty;
            if (parameter.ParameterType == typeof(CancellationToken))
                return cancellationToken;
            if (TryGetParameter(parameters, name, out var value))
                return ConvertArgument(value, parameter.ParameterType, name);
            if (DependencyInjectionEnabled && services?.GetService(parameter.ParameterType) is { } service)
                return service;
            if (parameter.HasDefaultValue)
                return parameter.DefaultValue;

            var reason = !DependencyInjectionEnabled ? "it was not supplied as a parameter"
                : services is null ? "it was not supplied as a parameter and no IServiceProvider was provided"
                : "it was not supplied as a parameter nor registered as a service";
            throw new ScriptBindingException(ScriptingLanguage.CSharp, name, $"Cannot bind delegate parameter '{name}' ({parameter.ParameterType}): {reason}.");
        }

        static bool TryGetParameter(IReadOnlyDictionary<string, object> parameters, string name, out object? value)
        {
            if (parameters.TryGetValue(name, out var exact))
            {
                value = exact;
                return true;
            }
            foreach (var (key, candidate) in parameters)
            {
                if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = candidate;
                    return true;
                }
            }
            value = null;
            return false;
        }

        static object? ConvertArgument(object? value, Type targetType, string name)
        {
            if (value is null || targetType.IsInstanceOfType(value))
                return value;

            var underlyingType = Nullable.GetUnderlyingType(targetType) ?? targetType;
            var error = $"Cannot convert parameter '{name}' from {value.GetType()} to {targetType}.";
            try
            {
                if (value is JsonElement json)
                    return json.Deserialize(targetType, JsonSerializerOptions.Web);
                var converter = TypeDescriptor.GetConverter(underlyingType);
                if (converter.CanConvertFrom(value.GetType()))
                    return converter.ConvertFrom(null, CultureInfo.InvariantCulture, value);
                if (value is IConvertible && typeof(IConvertible).IsAssignableFrom(underlyingType))
                    return Convert.ChangeType(value, underlyingType, CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException or ArgumentException or NotSupportedException or JsonException)
            {
                throw new ScriptBindingException(ScriptingLanguage.CSharp, name, error, ex);
            }
            throw new ScriptBindingException(ScriptingLanguage.CSharp, name, error);
        }

        static async Task<object?> AwaitResultAsync(object? result, Type returnType)
        {
            switch (result)
            {
                case null:
                    return null;
                case Task task when typeof(Task).IsAssignableFrom(returnType):
                    await task.ConfigureAwait(false);
                    return returnType.IsGenericType
                        ? returnType.GetProperty(nameof(Task<object>.Result))!.GetValue(task)
                        : null;
                case ValueTask valueTask:
                    await valueTask.ConfigureAwait(false);
                    return null;
            }
            if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(ValueTask<>))
            {
                var task = (Task)returnType.GetMethod(nameof(ValueTask<object>.AsTask))!.Invoke(result, null)!;
                await task.ConfigureAwait(false);
                return task.GetType().GetProperty(nameof(Task<object>.Result))!.GetValue(task);
            }
            return result;
        }

        static string[] Errors(IEnumerable<Diagnostic> diagnostics)
            => [.. diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).Select(diagnostic => diagnostic.ToString())];

        static bool EndsWithTypedLambda(CompilationUnitSyntax root)
            => root.Members.LastOrDefault() is GlobalStatementSyntax { Statement: ExpressionStatementSyntax { Expression: ParenthesizedLambdaExpressionSyntax lambda } statement }
                && statement.SemicolonToken.IsMissing
                && lambda.ParameterList.Parameters.All(parameter => parameter.Type is not null);
    }
}

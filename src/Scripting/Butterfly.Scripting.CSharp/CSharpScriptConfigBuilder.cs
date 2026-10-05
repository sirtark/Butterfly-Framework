using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Configuration;
using System.Globalization;
using System.Reflection;

namespace Butterfly.Scripting.CSharp
{
    public sealed class CSharpScriptConfigBuilder
    {
        readonly List<Assembly> _references = [];
        readonly List<string> _imports = [];

        CSharpScriptMode _mode = CSharpScriptMode.Parameters;
        CSharpScriptFlags _flags = CSharpScriptFlags.None;
        Type? _contextType;
        int _compilationCacheSize;

        public CSharpScriptConfigBuilder WithReference(Assembly assembly)
        {
            _references.Add(assembly);
            return this;
        }
        public CSharpScriptConfigBuilder WithReferences(params Assembly[] assemblies)
        {
            _references.AddRange(assemblies);
            return this;
        }

        public CSharpScriptConfigBuilder WithImport(string import)
        {
            _imports.Add(import);
            return this;
        }
        public CSharpScriptConfigBuilder WithImport(params string[] imports)
        {
            _imports.AddRange(imports);
            return this;
        }

        public CSharpScriptConfigBuilder WithDefaults()
            => WithReferences(typeof(object).Assembly, typeof(Console).Assembly, typeof(Enumerable).Assembly)
                .WithImport("System", "System.Collections.Generic", "System.Linq", "System.Text", "System.Threading", "System.Threading.Tasks");
        public CSharpScriptConfigBuilder WithType<T>()
            => WithType(typeof(T));
        public CSharpScriptConfigBuilder WithType(Type type)
        {
            ArgumentNullException.ThrowIfNull(type, nameof(type));
            WithReference(type.Assembly);
            return type.Namespace is { Length: > 0 } @namespace ? WithImport(@namespace) : this;
        }

        public CSharpScriptConfigBuilder UseExpressionMode()
        {
            _mode = CSharpScriptMode.Expression;
            return this;
        }
        public CSharpScriptConfigBuilder UseParametersMode()
        {
            _mode = CSharpScriptMode.Parameters;
            return this;
        }
        public CSharpScriptConfigBuilder UseDelegateMode()
        {
            _mode = CSharpScriptMode.Delegate;
            return this;
        }
        public CSharpScriptConfigBuilder UseDynamicMode()
        {
            _mode = CSharpScriptMode.Dynamic;
            return this;
        }
        public CSharpScriptConfigBuilder UseContextMode<TContext>() where TContext : class
            => UseContextMode(typeof(TContext));
        public CSharpScriptConfigBuilder UseContextMode(Type contextType)
        {
            ArgumentNullException.ThrowIfNull(contextType, nameof(contextType));
            _mode = CSharpScriptMode.Context;
            _contextType = contextType;
            return this;
        }

        public CSharpScriptConfigBuilder AllowDependencyInjection()
        {
            _flags |= CSharpScriptFlags.DependencyInjection;
            return this;
        }

        public CSharpScriptConfigBuilder WithCompilationCache(int maxEntries = 256)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(maxEntries, nameof(maxEntries));
            _compilationCacheSize = maxEntries;
            return this;
        }

        public CSharpScriptConfigBuilder FromConfiguration(IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration, nameof(configuration));

            if (bool.TryParse(configuration["UseDefaults"], out var useDefaults) && useDefaults)
                WithDefaults();
            foreach (var reference in Values(configuration.GetSection("References")))
                WithReference(Assembly.Load(reference));
            foreach (var import in Values(configuration.GetSection("Imports")))
                WithImport(import);
            if (configuration["Mode"] is { Length: > 0 } mode)
                _mode = Enum.Parse<CSharpScriptMode>(mode, ignoreCase: true);
            if (configuration["ContextType"] is { Length: > 0 } contextType)
                UseContextMode(Type.GetType(contextType, throwOnError: true)!);
            if (bool.TryParse(configuration["DependencyInjection"], out var dependencyInjection) && dependencyInjection)
                AllowDependencyInjection();
            if (int.TryParse(configuration["CompilationCacheSize"], CultureInfo.InvariantCulture, out var cacheSize))
                WithCompilationCache(cacheSize);
            return this;

            static IEnumerable<string> Values(IConfigurationSection section)
                => section.GetChildren().Select(child => child.Value).OfType<string>().Where(value => value.Length > 0);
        }

        public CSharpScriptConfig Build()
            => new()
            {
                References = [.. _references.Distinct()],
                Imports = [.. _imports.Distinct()],

                Mode = _mode,
                Flags = _flags,
                ContextType = _mode == CSharpScriptMode.Context ? _contextType : null,
                CompilationCacheSize = _compilationCacheSize
            };
    }
}

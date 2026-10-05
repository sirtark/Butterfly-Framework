using System.Reflection;

namespace Butterfly.Scripting
{
    public sealed class MappedScriptInvoker : IScriptInvoker
    {
        readonly Dictionary<ScriptingLanguage, IScriptExecutor> _executors;

        public MappedScriptInvoker() : this(AppDomain.CurrentDomain.GetAssemblies()) { }
        public MappedScriptInvoker(IEnumerable<Assembly> assemblies)
        {
            ArgumentNullException.ThrowIfNull(assemblies, nameof(assemblies));
            _executors = ScriptExecutorDiscovery.CreateExecutors(assemblies).ToDictionary(executor => executor.Lenguage);
        }

        public Task<object?> InvokeScriptAsync(ScriptingLanguage language, string scriptContent, IReadOnlyDictionary<string, object> parameters, IServiceProvider? services = null, CancellationToken cancellationToken = default)
            => ScriptExecution.InvokeAsync(_executors, language, scriptContent, parameters, services, cancellationToken);
    }
}

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Reflection;

namespace Butterfly.Scripting
{
    public sealed class ScriptInvokerBuilder
    {
        readonly Dictionary<ScriptingLanguage, IScriptExecutor> _executors = [];
        readonly List<Func<IScriptInvoker, IScriptInvoker>> _decorators = [];

        public ScriptInvokerBuilder() { }
        public ScriptInvokerBuilder(IServiceProvider services)
        {
            Services = services ?? throw new ArgumentNullException(nameof(services));
        }

        public IServiceProvider? Services { get; }

        public ScriptInvokerBuilder AddExecutor(IScriptExecutor executor)
        {
            if (_executors.TryAdd(executor.Lenguage, executor))
                return this;
            throw new InvalidOperationException($"Trying to add multiple script executor to language: {executor.Lenguage}.");
        }
        public ScriptInvokerBuilder AddExecutor(Func<IScriptExecutor> builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            var executor = builder();
            ArgumentNullException.ThrowIfNull(executor, "The factory returned null.");
            return AddExecutor(executor);
        }
        public ScriptInvokerBuilder AddExecutorsFromAssembly(Assembly assembly)
        {
            ArgumentNullException.ThrowIfNull(assembly, nameof(assembly));
            foreach (var executor in ScriptExecutorDiscovery.CreateExecutors([assembly]))
                AddExecutor(executor);
            return this;
        }

        public ScriptInvokerBuilder Decorate(Func<IScriptInvoker, IScriptInvoker> decorator)
        {
            ArgumentNullException.ThrowIfNull(decorator, nameof(decorator));
            _decorators.Add(decorator);
            return this;
        }
        public ScriptInvokerBuilder WithTimeout(TimeSpan timeout)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero, nameof(timeout));
            return Decorate(inner => new TimeoutScriptInvoker(inner, timeout));
        }
        public ScriptInvokerBuilder WithLogging(ILogger? logger = null)
            => Decorate(inner => new LoggingScriptInvoker(inner, logger ?? (Services?.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance).CreateLogger(ScriptingTelemetry.Name)));
        public ScriptInvokerBuilder WithTelemetry()
            => Decorate(inner => new TelemetryScriptInvoker(inner));

        public IScriptInvoker Build()
            => _decorators.Aggregate<Func<IScriptInvoker, IScriptInvoker>, IScriptInvoker>(new BuiltScriptInvoker(new(_executors)), (inner, decorate) => decorate(inner));
    }
}

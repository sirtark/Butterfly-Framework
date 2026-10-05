using System.Reflection;

namespace Butterfly.Scripting.Lua
{
    public sealed class LuaScriptExecutor : IScriptExecutor
    {
        readonly LuaScriptConfig _config;

        public LuaScriptExecutor(LuaScriptConfig config)
        {
            ArgumentNullException.ThrowIfNull(config, nameof(config));
            if (config.Engine is null)
                throw new ArgumentException("The configuration requires a Lua engine.", nameof(config));
            _config = config;
        }

        public ScriptingLanguage Lenguage => ScriptingLanguage.Lua;

        public ILuaEngine Engine => _config.Engine;
        public LuaLibraries Libraries => _config.Libraries;

        public ValueTask<ScriptValidationResult> ValidateAsync(string scriptContent, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(string.IsNullOrWhiteSpace(scriptContent)
                ? ScriptValidationResult.Failure("The script is empty.")
                : _config.Engine.Validate(scriptContent));
        }

        public Task<object?> ExecuteAsync(string scriptContent, IReadOnlyDictionary<string, object> parameters, IServiceProvider? services = null, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(parameters, nameof(parameters));
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var results = _config.Engine.Execute(scriptContent, ToGlobals(parameters), _config.Libraries, cancellationToken);
                return Task.FromResult<object?>(results.Count switch
                {
                    0 => null,
                    1 => results[0],
                    _ => results.ToList()
                });
            }
            catch (Exception ex)
            {
                return Task.FromException<object?>(ex);
            }
        }

        static Dictionary<string, object?> ToGlobals(IReadOnlyDictionary<string, object> parameters)
        {
            var globals = new Dictionary<string, object?>(parameters.Count);
            foreach (var (name, value) in parameters)
            {
                try
                {
                    globals[name] = LuaValues.FromClr(value);
                }
                catch (Exception ex) when (ex is NotSupportedException or ArgumentException or InvalidOperationException or TargetInvocationException)
                {
                    throw new ScriptBindingException(ScriptingLanguage.Lua, name, $"Cannot convert parameter '{name}' ({value?.GetType()}) to a Lua value: {ex.Message}", ex);
                }
            }
            return globals;
        }
    }
}

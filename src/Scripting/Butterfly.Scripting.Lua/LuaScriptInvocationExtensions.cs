namespace Butterfly.Scripting.Lua
{
    public static class LuaScriptInvocationExtensions
    {
        public static Task<object?> InvokeLuaAsync(this IScriptInvoker invoker, string scriptContent, object? parameters = null, IServiceProvider? services = null, CancellationToken cancellationToken = default)
            => invoker.InvokeScriptAsync(ScriptingLanguage.Lua, scriptContent, parameters, services, cancellationToken);
        public static Task<T?> InvokeLuaAsync<T>(this IScriptInvoker invoker, string scriptContent, object? parameters = null, IServiceProvider? services = null, CancellationToken cancellationToken = default)
            => invoker.InvokeScriptAsync<T>(ScriptingLanguage.Lua, scriptContent, parameters, services, cancellationToken);
    }
}

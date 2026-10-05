namespace Butterfly.Scripting.CSharp
{
    public static class CSharpScriptInvocationExtensions
    {
        public static Task<object?> InvokeCSharpAsync(this IScriptInvoker invoker, string scriptContent, object? parameters = null, IServiceProvider? services = null, CancellationToken cancellationToken = default)
            => invoker.InvokeScriptAsync(ScriptingLanguage.CSharp, scriptContent, parameters, services, cancellationToken);
        public static Task<T?> InvokeCSharpAsync<T>(this IScriptInvoker invoker, string scriptContent, object? parameters = null, IServiceProvider? services = null, CancellationToken cancellationToken = default)
            => invoker.InvokeScriptAsync<T>(ScriptingLanguage.CSharp, scriptContent, parameters, services, cancellationToken);
    }
}

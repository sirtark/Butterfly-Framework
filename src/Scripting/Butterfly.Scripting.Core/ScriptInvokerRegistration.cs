namespace Butterfly.Scripting
{
    internal sealed class ScriptInvokerRegistration(IScriptInvoker invoker)
    {
        public IScriptInvoker Invoker { get; } = invoker;
    }
}

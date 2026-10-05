namespace Butterfly.Scripting.CSharp
{
    public static class ScriptInvokerExtensions
    {
        public static ScriptInvokerBuilder AddCSharp(this ScriptInvokerBuilder invokerBuilder)
        {
            invokerBuilder.AddExecutor(new CSharpScriptExecutor());
            return invokerBuilder;
        }
        public static ScriptInvokerBuilder AddCSharp(this ScriptInvokerBuilder invokerBuilder, CSharpScriptConfigBuilder configBuilder)
        {
            ArgumentNullException.ThrowIfNull(configBuilder, nameof(configBuilder));
            invokerBuilder.AddExecutor(new CSharpScriptExecutor(configBuilder.Build()));
            return invokerBuilder;
        }
        public static ScriptInvokerBuilder AddCSharp(this ScriptInvokerBuilder invokerBuilder, Func<CSharpScriptConfigBuilder, CSharpScriptConfig> builder)
        {
            ArgumentNullException.ThrowIfNull(builder, nameof(builder));
            invokerBuilder.AddCSharp(builder.Invoke(new CSharpScriptConfigBuilder()));
            return invokerBuilder;
        }
        public static ScriptInvokerBuilder AddCSharp(this ScriptInvokerBuilder invokerBuilder, Action<CSharpScriptConfigBuilder> configurator)
        {
            ArgumentNullException.ThrowIfNull(configurator, nameof(configurator));
            invokerBuilder.AddCSharp(builder => { configurator.Invoke(builder); return builder.Build(); });
            return invokerBuilder;
        }
        public static ScriptInvokerBuilder AddCSharp(this ScriptInvokerBuilder invokerBuilder, CSharpScriptConfig config)
        {
            ArgumentNullException.ThrowIfNull(config, nameof(config));
            return invokerBuilder.AddExecutor(new CSharpScriptExecutor(config));
        }
    }
}

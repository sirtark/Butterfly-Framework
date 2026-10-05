namespace Butterfly.Scripting.Lua
{
    public static class ScriptInvokerExtensions
    {
        public static ScriptInvokerBuilder AddLua(this ScriptInvokerBuilder invokerBuilder, LuaScriptConfig config)
        {
            ArgumentNullException.ThrowIfNull(config, nameof(config));
            return invokerBuilder.AddExecutor(new LuaScriptExecutor(config));
        }
        public static ScriptInvokerBuilder AddLua(this ScriptInvokerBuilder invokerBuilder, LuaScriptConfigBuilder configBuilder)
        {
            ArgumentNullException.ThrowIfNull(configBuilder, nameof(configBuilder));
            return invokerBuilder.AddLua(configBuilder.Build());
        }
        public static ScriptInvokerBuilder AddLua(this ScriptInvokerBuilder invokerBuilder, Func<LuaScriptConfigBuilder, LuaScriptConfig> builder)
        {
            ArgumentNullException.ThrowIfNull(builder, nameof(builder));
            return invokerBuilder.AddLua(builder.Invoke(new LuaScriptConfigBuilder()));
        }
        public static ScriptInvokerBuilder AddLua(this ScriptInvokerBuilder invokerBuilder, Action<LuaScriptConfigBuilder> configurator)
        {
            ArgumentNullException.ThrowIfNull(configurator, nameof(configurator));
            return invokerBuilder.AddLua(builder => { configurator.Invoke(builder); return builder.Build(); });
        }
    }
}

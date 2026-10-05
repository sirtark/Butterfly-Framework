namespace Butterfly.Scripting.Lua.NLua
{
    public static class LuaScriptConfigBuilderExtensions
    {
        public static LuaScriptConfigBuilder UseNLua(this LuaScriptConfigBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder, nameof(builder));
            return builder.UseEngine(new NLuaEngine());
        }
    }
}

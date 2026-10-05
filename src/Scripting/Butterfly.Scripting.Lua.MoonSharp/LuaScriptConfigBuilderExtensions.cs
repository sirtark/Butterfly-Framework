namespace Butterfly.Scripting.Lua.MoonSharp
{
    public static class LuaScriptConfigBuilderExtensions
    {
        public static LuaScriptConfigBuilder UseMoonSharp(this LuaScriptConfigBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder, nameof(builder));
            return builder.UseEngine(new MoonSharpLuaEngine());
        }
    }
}

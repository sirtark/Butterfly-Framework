namespace Butterfly.Scripting.Lua
{
    public sealed class LuaScriptConfig
    {
        public required ILuaEngine Engine { get; init; }
        public LuaLibraries Libraries { get; init; } = LuaLibraries.Safe;
    }
}

namespace Butterfly.Scripting.Lua
{
    [Flags]
    public enum LuaLibraries : uint
    {
        None,
        Base = 1 << 0,
        String = 1 << 1,
        Table = 1 << 2,
        Math = 1 << 3,
        Coroutine = 1 << 4,
        OsTime = 1 << 5,
        Load = 1 << 6,

        // These give the script access to the file system, the process or the interpreter internals.
        Modules = 1 << 7,
        OsSystem = 1 << 8,
        IO = 1 << 9,
        Debug = 1 << 10,

        Safe = Base | String | Table | Math | Coroutine | OsTime,
        All = Safe | Load | Modules | OsSystem | IO | Debug
    }
}

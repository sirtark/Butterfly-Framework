namespace Butterfly.Scripting.Lua
{
    // Globals and results are plain .NET values: null, bool, long, double, string,
    // IReadOnlyList<object?> for sequences and IReadOnlyDictionary<string, object?> for any other table.
    public interface ILuaEngine
    {
        ScriptValidationResult Validate(string scriptContent);
        IReadOnlyList<object?> Execute(string scriptContent, IReadOnlyDictionary<string, object?> globals, LuaLibraries libraries, CancellationToken cancellationToken);
    }
}

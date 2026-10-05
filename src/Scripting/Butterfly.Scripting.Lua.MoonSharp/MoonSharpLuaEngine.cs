using MoonSharp.Interpreter;

namespace Butterfly.Scripting.Lua.MoonSharp
{
    public sealed class MoonSharpLuaEngine : ILuaEngine
    {
        const string ChunkName = "script";

        static readonly (LuaLibraries Library, CoreModules Modules)[] ModuleMap =
        [
            (LuaLibraries.Base, CoreModules.Basic | CoreModules.GlobalConsts | CoreModules.TableIterators | CoreModules.Metatables | CoreModules.ErrorHandling),
            (LuaLibraries.String, CoreModules.String),
            (LuaLibraries.Table, CoreModules.Table),
            (LuaLibraries.Math, CoreModules.Math | CoreModules.Bit32),
            (LuaLibraries.Coroutine, CoreModules.Coroutine),
            (LuaLibraries.OsTime, CoreModules.OS_Time),
            (LuaLibraries.Load, CoreModules.LoadMethods),
            (LuaLibraries.Modules, CoreModules.LoadMethods),
            (LuaLibraries.OsSystem, CoreModules.OS_System),
            (LuaLibraries.IO, CoreModules.IO),
            (LuaLibraries.Debug, CoreModules.Debug)
        ];
        static readonly string[] LoadFunctions = ["load", "loadsafe"];
        static readonly string[] ModuleFunctions = ["dofile", "loadfile", "loadfilesafe", "require"];

        public ScriptValidationResult Validate(string scriptContent)
        {
            try
            {
                new Script(CoreModules.None).LoadString(scriptContent, codeFriendlyName: ChunkName);
                return ScriptValidationResult.Success;
            }
            catch (SyntaxErrorException ex)
            {
                return ScriptValidationResult.Failure(ex.DecoratedMessage ?? ex.Message);
            }
        }

        public IReadOnlyList<object?> Execute(string scriptContent, IReadOnlyDictionary<string, object?> globals, LuaLibraries libraries, CancellationToken cancellationToken)
        {
            var script = CreateScript(libraries);
            if (cancellationToken.CanBeCanceled)
                script.AttachDebugger(new CancellationDebugger(cancellationToken));
            foreach (var (name, value) in globals)
                script.Globals.Set(name, ToDynValue(script, value));

            DynValue result;
            try
            {
                result = script.DoString(scriptContent, codeFriendlyName: ChunkName);
            }
            catch (InterpreterException ex)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new ScriptException(ScriptingLanguage.Lua, ex.DecoratedMessage ?? ex.Message, ex);
            }

            var visiting = new HashSet<Table>(ReferenceEqualityComparer.Instance);
            return result.Type switch
            {
                DataType.Void => [],
                DataType.Tuple => [.. result.Tuple.Select(value => ToClr(value, visiting))],
                _ => [ToClr(result, visiting)]
            };
        }

        static Script CreateScript(LuaLibraries libraries)
        {
            var modules = ModuleMap
                .Where(entry => libraries.HasFlag(entry.Library))
                .Aggregate(CoreModules.None, (all, entry) => all | entry.Modules);
            var script = new Script(modules);

            if (!libraries.HasFlag(LuaLibraries.Load))
                RemoveGlobals(script, LoadFunctions);
            if (!libraries.HasFlag(LuaLibraries.Modules))
                RemoveGlobals(script, ModuleFunctions);
            return script;
        }

        static void RemoveGlobals(Script script, string[] names)
        {
            foreach (var name in names)
                script.Globals.Remove(name);
        }

        static DynValue ToDynValue(Script script, object? value)
        {
            switch (value)
            {
                case null:
                    return DynValue.Nil;
                case bool boolean:
                    return DynValue.NewBoolean(boolean);
                case long integer:
                    return DynValue.NewNumber(integer);
                case double number:
                    return DynValue.NewNumber(number);
                case string text:
                    return DynValue.NewString(text);
                case IReadOnlyList<object?> list:
                    var sequence = new Table(script);
                    for (var i = 0; i < list.Count; i++)
                        sequence.Set(i + 1, ToDynValue(script, list[i]));
                    return DynValue.NewTable(sequence);
                case IReadOnlyDictionary<string, object?> dictionary:
                    var table = new Table(script);
                    foreach (var (key, item) in dictionary)
                        table.Set(key, ToDynValue(script, item));
                    return DynValue.NewTable(table);
                default:
                    throw new ArgumentException($"{value.GetType()} is not a Lua value.", nameof(value));
            }
        }

        static object? ToClr(DynValue value, HashSet<Table> visiting)
            => value.Type switch
            {
                DataType.Nil or DataType.Void => null,
                DataType.Boolean => value.Boolean,
                DataType.Number => LuaValues.FromNumber(value.Number),
                DataType.String => value.String,
                DataType.Table => ToClr(value.Table, visiting),
                _ => throw new ScriptException(ScriptingLanguage.Lua, $"The Lua script returned a {value.Type.ToString().ToLowerInvariant()} value, which cannot be converted to .NET.")
            };

        static object ToClr(Table table, HashSet<Table> visiting)
        {
            if (!visiting.Add(table))
                throw new ScriptException(ScriptingLanguage.Lua, "The Lua script returned a table that contains itself.");
            if (visiting.Count > LuaValues.MaxDepth)
                throw new ScriptException(ScriptingLanguage.Lua, $"The Lua script returned tables nested more than {LuaValues.MaxDepth} levels deep.");
            try
            {
                // MoonSharp keeps keys assigned nil by a table constructor, which in Lua do not exist.
                return LuaValues.FromTable(table.Pairs
                    .Where(pair => pair.Value.IsNotNil())
                    .Select(pair => new KeyValuePair<object, object?>(ToClr(pair.Key, visiting)!, ToClr(pair.Value, visiting))));
            }
            finally
            {
                visiting.Remove(table);
            }
        }
    }
}

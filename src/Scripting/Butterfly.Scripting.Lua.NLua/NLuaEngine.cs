using KeraLua;
using System.Reflection;
using System.Text;
using LuaState = KeraLua.Lua;
using NLuaState = NLua.Lua;

namespace Butterfly.Scripting.Lua.NLua
{
    public sealed class NLuaEngine : ILuaEngine
    {
        const string ChunkName = "=script";
        const string TextMode = "t";
        const int MultipleResults = -1;

        static readonly (LuaLibraries Library, string Name)[] LibraryNames =
        [
            (LuaLibraries.Base, "base"),
            (LuaLibraries.String, "string"),
            (LuaLibraries.Table, "table"),
            (LuaLibraries.Math, "math"),
            (LuaLibraries.Coroutine, "coroutine"),
            (LuaLibraries.OsTime, "ostime"),
            (LuaLibraries.Load, "load"),
            (LuaLibraries.Modules, "modules"),
            (LuaLibraries.OsSystem, "ossystem"),
            (LuaLibraries.IO, "io"),
            (LuaLibraries.Debug, "debug")
        ];

        // The script runs with this table as _ENV, so NLua's CLR bridge (luanet) and any library that was not allowed stay out of reach.
        // Cancellation is raised from a Lua hook instead of from .NET, so no managed exception ever unwinds through native frames.
        static readonly byte[] SandboxChunk;

        static NLuaEngine()
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Butterfly.Scripting.Lua.NLua.Scripts.SandboxChunk.lua");
            using var reader = new BinaryReader(stream!);
            SandboxChunk = reader.ReadBytes((int)stream!.Length);
        }

        public ScriptValidationResult Validate(string scriptContent)
        {
            using var state = new LuaState(openLibs: false) { Encoding = Encoding.UTF8 };
            return Load(state, scriptContent) == LuaStatus.OK ? ScriptValidationResult.Success : ScriptValidationResult.Failure(ErrorMessage(state));
        }

        public IReadOnlyList<object?> Execute(string scriptContent, IReadOnlyDictionary<string, object?> globals, LuaLibraries libraries, CancellationToken cancellationToken)
        {
            LuaFunction stop = handle =>
            {
                LuaState.FromIntPtr(handle).PushBoolean(cancellationToken.IsCancellationRequested);
                return 1;
            };

            var lua = new NLuaState();
            try
            {
                var state = lua.State;
                state.Encoding = Encoding.UTF8;

                var environment = CreateEnvironment(state, libraries, stop);
                foreach (var (name, value) in globals)
                {
                    Push(state, value);
                    state.SetField(environment, name);
                }

                if (Load(state, scriptContent) != LuaStatus.OK)
                    throw new ScriptException(ScriptingLanguage.Lua, ErrorMessage(state));
                state.PushCopy(environment);
                state.SetUpValue(-2, 1);

                if (state.PCall(0, MultipleResults, 0) != LuaStatus.OK)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new ScriptException(ScriptingLanguage.Lua, ErrorMessage(state));
                }

                var visiting = new HashSet<IntPtr>();
                var results = new object?[state.GetTop() - environment];
                for (var i = 0; i < results.Length; i++)
                    results[i] = ToClr(state, environment + 1 + i, visiting);
                return results;
            }
            finally
            {
                lua.Dispose();
                GC.KeepAlive(stop);
            }
        }

        static LuaStatus Load(LuaState state, string scriptContent)
            => state.LoadBuffer(Encoding.UTF8.GetBytes(scriptContent), ChunkName, TextMode);

        static int CreateEnvironment(LuaState state, LuaLibraries libraries, LuaFunction stop)
        {
            if (state.LoadBuffer(SandboxChunk, "=sandbox", TextMode) != LuaStatus.OK)
                throw new InvalidOperationException(ErrorMessage(state));

            state.PushCFunction(stop);
            state.CreateTable(0, LibraryNames.Length);
            foreach (var (library, name) in LibraryNames)
            {
                state.PushBoolean(libraries.HasFlag(library));
                state.SetField(-2, name);
            }

            if (state.PCall(2, 1, 0) != LuaStatus.OK)
                throw new InvalidOperationException(ErrorMessage(state));
            return state.GetTop();
        }

        static string ErrorMessage(LuaState state)
            => state.Type(-1) is LuaType.String or LuaType.Number
                ? state.ToString(-1, callMetamethod: false)
                : $"(error object is a {state.TypeName(-1)} value)";

        static void Push(LuaState state, object? value)
        {
            if (!state.CheckStack(3))
                throw new ScriptException(ScriptingLanguage.Lua, "The Lua stack overflowed while passing the parameters.");

            switch (value)
            {
                case null:
                    state.PushNil();
                    break;
                case bool boolean:
                    state.PushBoolean(boolean);
                    break;
                case long integer:
                    state.PushInteger(integer);
                    break;
                case double number:
                    state.PushNumber(number);
                    break;
                case string text:
                    state.PushString(text);
                    break;
                case IReadOnlyList<object?> list:
                    state.CreateTable(list.Count, 0);
                    for (var i = 0; i < list.Count; i++)
                    {
                        Push(state, list[i]);
                        state.RawSetInteger(-2, i + 1);
                    }
                    break;
                case IReadOnlyDictionary<string, object?> dictionary:
                    state.CreateTable(0, dictionary.Count);
                    foreach (var (key, item) in dictionary)
                    {
                        state.PushString(key);
                        Push(state, item);
                        state.RawSet(-3);
                    }
                    break;
                default:
                    throw new ArgumentException($"{value.GetType()} is not a Lua value.", nameof(value));
            }
        }

        static object? ToClr(LuaState state, int index, HashSet<IntPtr> visiting)
            => state.Type(index) switch
            {
                LuaType.Nil or LuaType.None => null,
                LuaType.Boolean => state.ToBoolean(index),
                LuaType.Number => state.IsInteger(index) ? state.ToInteger(index) : LuaValues.FromNumber(state.ToNumber(index)),
                LuaType.String => state.ToString(index, callMetamethod: false),
                LuaType.Table => TableToClr(state, index, visiting),
                var type => throw new ScriptException(ScriptingLanguage.Lua, $"The Lua script returned a {state.TypeName(type)} value, which cannot be converted to .NET.")
            };

        static object TableToClr(LuaState state, int index, HashSet<IntPtr> visiting)
        {
            var pointer = state.ToPointer(index);
            if (!visiting.Add(pointer))
                throw new ScriptException(ScriptingLanguage.Lua, "The Lua script returned a table that contains itself.");
            if (visiting.Count > LuaValues.MaxDepth || !state.CheckStack(3))
                throw new ScriptException(ScriptingLanguage.Lua, $"The Lua script returned tables nested more than {LuaValues.MaxDepth} levels deep.");
            try
            {
                var table = state.AbsIndex(index);
                var entries = new List<KeyValuePair<object, object?>>();
                state.PushNil();
                while (state.Next(table))
                {
                    entries.Add(new(ToClr(state, -2, visiting)!, ToClr(state, -1, visiting)));
                    state.Pop(1);
                }
                return LuaValues.FromTable(entries);
            }
            finally
            {
                visiting.Remove(pointer);
            }
        }
    }
}

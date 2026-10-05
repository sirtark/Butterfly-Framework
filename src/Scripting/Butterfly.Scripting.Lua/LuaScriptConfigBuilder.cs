using Microsoft.Extensions.Configuration;

namespace Butterfly.Scripting.Lua
{
    public sealed class LuaScriptConfigBuilder
    {
        ILuaEngine? _engine;
        LuaLibraries _libraries = LuaLibraries.Safe;

        public LuaScriptConfigBuilder UseEngine(ILuaEngine engine)
        {
            ArgumentNullException.ThrowIfNull(engine, nameof(engine));
            _engine = engine;
            return this;
        }

        public LuaScriptConfigBuilder WithLibraries(LuaLibraries libraries)
        {
            _libraries = libraries;
            return this;
        }
        public LuaScriptConfigBuilder AllowLibraries(LuaLibraries libraries)
        {
            _libraries |= libraries;
            return this;
        }

        public LuaScriptConfigBuilder FromConfiguration(IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration, nameof(configuration));

            if (configuration["Libraries"] is { Length: > 0 } libraries)
                _libraries = Enum.Parse<LuaLibraries>(libraries, ignoreCase: true);
            return this;
        }

        public LuaScriptConfig Build()
            => new()
            {
                Engine = _engine ?? throw new InvalidOperationException("No Lua engine was configured. Call UseEngine or an engine extension such as UseMoonSharp() or UseNLua()."),
                Libraries = _libraries
            };
    }
}

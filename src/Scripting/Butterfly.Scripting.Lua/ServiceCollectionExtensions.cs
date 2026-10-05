using Microsoft.Extensions.DependencyInjection;

namespace Butterfly.Scripting.Lua
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddLuaScriptInvoker(this IServiceCollection services, Func<LuaScriptConfigBuilder, LuaScriptConfig> builder)
        {
            ArgumentNullException.ThrowIfNull(builder, nameof(builder));
            return services.AddScriptInvoker(invokerBuilder => invokerBuilder.AddLua(builder));
        }
        public static IServiceCollection AddLuaScriptInvoker(this IServiceCollection services, Action<LuaScriptConfigBuilder> configurator)
        {
            ArgumentNullException.ThrowIfNull(configurator, nameof(configurator));
            return services.AddScriptInvoker(invokerBuilder => invokerBuilder.AddLua(configurator));
        }

        public static IServiceCollection AddKeyedLuaScriptInvoker(this IServiceCollection services, string key, Func<LuaScriptConfigBuilder, LuaScriptConfig> builder)
        {
            ArgumentException.ThrowIfNullOrEmpty(key, nameof(key));
            ArgumentNullException.ThrowIfNull(builder, nameof(builder));
            return services.AddKeyedScriptInvoker(key, invokerBuilder => invokerBuilder.AddLua(builder));
        }
        public static IServiceCollection AddKeyedLuaScriptInvoker(this IServiceCollection services, string key, Action<LuaScriptConfigBuilder> configurator)
        {
            ArgumentException.ThrowIfNullOrEmpty(key, nameof(key));
            ArgumentNullException.ThrowIfNull(configurator, nameof(configurator));
            return services.AddKeyedScriptInvoker(key, invokerBuilder => invokerBuilder.AddLua(configurator));
        }
    }
}

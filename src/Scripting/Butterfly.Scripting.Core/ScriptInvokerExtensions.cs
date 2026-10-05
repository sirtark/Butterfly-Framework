using Microsoft.Extensions.DependencyInjection;
using System.Reflection;

namespace Butterfly.Scripting
{
    public static class ScriptInvokerExtensions
    {
        public static IServiceCollection AddMappedScriptInvoker(this IServiceCollection services, params Assembly[] assemblies)
            => services.AddScriptInvoker(_ => assemblies.Length == 0 ? new MappedScriptInvoker() : new MappedScriptInvoker(assemblies));
        public static IServiceCollection AddScriptInvoker(this IServiceCollection services, Func<ScriptInvokerBuilder, IScriptInvoker> builder)
        {
            ArgumentNullException.ThrowIfNull(builder, nameof(builder));
            return services.AddScriptInvokerCore(serviceKey: null, builder);
        }
        public static IServiceCollection AddScriptInvoker(this IServiceCollection services, Action<ScriptInvokerBuilder> configurator)
        {
            ArgumentNullException.ThrowIfNull(configurator, nameof(configurator));
            return services.AddScriptInvoker(builder => { configurator.Invoke(builder); return builder.Build(); });
        }

        public static IServiceCollection AddKeyedScriptInvoker(this IServiceCollection services, string key, Func<ScriptInvokerBuilder, IScriptInvoker> builder)
        {
            ArgumentException.ThrowIfNullOrEmpty(key, nameof(key));
            ArgumentNullException.ThrowIfNull(builder, nameof(builder));
            return services.AddScriptInvokerCore(key, builder);
        }
        public static IServiceCollection AddKeyedScriptInvoker(this IServiceCollection services, string key, Action<ScriptInvokerBuilder> configurator)
        {
            ArgumentNullException.ThrowIfNull(configurator, nameof(configurator));
            return services.AddKeyedScriptInvoker(key, builder => { configurator.Invoke(builder); return builder.Build(); });
        }

        static IServiceCollection AddScriptInvokerCore(this IServiceCollection services, string? serviceKey, Func<ScriptInvokerBuilder, IScriptInvoker> builder)
        {
            var registrationKey = new object();
            services.AddKeyedSingleton(registrationKey, (provider, _) => new ScriptInvokerRegistration(builder(new ScriptInvokerBuilder(provider))));

            if (serviceKey is null)
                services.AddTransient<IScriptInvoker>(provider => new ServiceProviderScriptInvoker(provider.GetRequiredKeyedService<ScriptInvokerRegistration>(registrationKey).Invoker, provider));
            else
                services.AddKeyedTransient<IScriptInvoker>(serviceKey, (provider, _) => new ServiceProviderScriptInvoker(provider.GetRequiredKeyedService<ScriptInvokerRegistration>(registrationKey).Invoker, provider));
            return services;
        }
    }
}

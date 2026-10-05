using Microsoft.Extensions.DependencyInjection;

namespace Butterfly.Scripting.CSharp
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddCSharpScriptInvoker(this IServiceCollection services)
        {
            services.AddScriptInvoker(builder => builder.AddCSharp());
            return services;
        }
        public static IServiceCollection AddCSharpScriptInvoker(this IServiceCollection services, Func<CSharpScriptConfigBuilder, CSharpScriptConfig> builder)
        {
            ArgumentNullException.ThrowIfNull(builder, nameof(builder));
            services.AddScriptInvoker(invokerBuilder => invokerBuilder.AddCSharp(builder.Invoke(new CSharpScriptConfigBuilder())));
            return services;
        }
        public static IServiceCollection AddCSharpScriptInvoker(this IServiceCollection services, Action<CSharpScriptConfigBuilder> configurator)
        {
            ArgumentNullException.ThrowIfNull(configurator, nameof(configurator));
            services.AddCSharpScriptInvoker(builder => { configurator.Invoke(builder); return builder.Build(); });
            return services;
        }

        public static IServiceCollection AddKeyedCSharpScriptInvoker(this IServiceCollection services, string key)
        {
            ArgumentNullException.ThrowIfNullOrEmpty(key, nameof(key));
            services.AddKeyedScriptInvoker(key, builder => builder.AddCSharp());
            return services;
        }
        public static IServiceCollection AddKeyedCSharpScriptInvoker(this IServiceCollection services, string key, Func<CSharpScriptConfigBuilder, CSharpScriptConfig> builder)
        {
            ArgumentNullException.ThrowIfNullOrEmpty(key, nameof(key));
            ArgumentNullException.ThrowIfNull(builder, nameof(builder));
            services.AddKeyedScriptInvoker(key, invokerBuilder => invokerBuilder.AddCSharp(builder));
            return services;
        }
        public static IServiceCollection AddKeyedCSharpScriptInvoker(this IServiceCollection services, string key, Action<CSharpScriptConfigBuilder> configurator)
        {
            ArgumentNullException.ThrowIfNull(configurator, nameof(configurator));
            services.AddKeyedCSharpScriptInvoker(key, builder => { configurator.Invoke(builder); return builder.Build(); });
            return services;
        }
    }
}

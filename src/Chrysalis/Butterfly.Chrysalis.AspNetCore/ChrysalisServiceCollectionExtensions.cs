using Butterfly.Chrysalis.Binary;
using Butterfly.Chrysalis.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics.CodeAnalysis;

namespace Butterfly.Chrysalis.AspNetCore
{
    /// <summary>Configures the <see cref="ChrysalisServer"/> registered by <see cref="ChrysalisServiceCollectionExtensions.AddChrysalis"/>.</summary>
    public sealed class ChrysalisBuilder
    {
        private readonly List<Action<ChrysalisServerOptions>> configureOptions = [];
        private readonly List<Action<ChrysalisServer, IServiceProvider>> registrations = [];

        internal ChrysalisBuilder(IServiceCollection services)
        {
            Services = services;
        }

        public IServiceCollection Services { get; }

        public ChrysalisBuilder Configure(Action<ChrysalisServerOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);
            configureOptions.Add(configure);
            return this;
        }

        /// <summary>
        /// Exposes a service implemented by <typeparamref name="TImplementation"/>, resolved from dependency injection for every
        /// call (inside the request scope in ASP.NET Core, or a scope created for the call elsewhere).
        /// </summary>
        public ChrysalisBuilder Expose<TContract, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TImplementation>(ServiceLifetime lifetime = ServiceLifetime.Scoped)
            where TContract : class
            where TImplementation : class, TContract
        {
            Services.TryAdd(new ServiceDescriptor(typeof(TImplementation), typeof(TImplementation), lifetime));
            registrations.Add((server, root) => server.Expose<TContract>(call => call.GetServices(root).GetRequiredService<TImplementation>()));
            return this;
        }

        /// <summary>Exposes a service with a single implementation shared by every call.</summary>
        public ChrysalisBuilder Expose<TContract>(TContract implementation) where TContract : class
        {
            ArgumentNullException.ThrowIfNull(implementation);
            registrations.Add((server, _) => server.Expose(implementation));
            return this;
        }

        /// <summary>Adds middleware resolved from dependency injection for every call (it may take scoped dependencies).</summary>
        public ChrysalisBuilder Use<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TMiddleware>()
            where TMiddleware : class, IChrysalisMiddleware
        {
            Services.TryAddScoped<TMiddleware>();
            registrations.Add((server, root) => server.Use((call, next) => call.GetServices(root).GetRequiredService<TMiddleware>().InvokeAsync(call, next)));
            return this;
        }

        public ChrysalisBuilder Use(Func<ChrysalisCallContext, ChrysalisNext, ValueTask<object?>> middleware)
        {
            ArgumentNullException.ThrowIfNull(middleware);
            registrations.Add((server, _) => server.Use(middleware));
            return this;
        }

        internal ChrysalisServer Build(IServiceProvider root)
        {
            var options = new ChrysalisServerOptions
            {
                Logger = root.GetService<ILoggerFactory>()?.CreateLogger("Butterfly.Chrysalis") ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance
            };
            foreach (var configure in configureOptions)
                configure(options);

            var server = new ChrysalisServer(options);
            // First in the pipeline: every call gets a service scope and, in ASP.NET Core, the authenticated user.
            server.Use(ScopeMiddleware(root));
            foreach (var registration in registrations)
                registration(server, root);
            return server;
        }

        private static Func<ChrysalisCallContext, ChrysalisNext, ValueTask<object?>> ScopeMiddleware(IServiceProvider root) => async (call, next) =>
        {
            if (call.GetHttpContext()?.Items.TryGetValue(typeof(HttpContext), out var item) == true && item is HttpContext http)
            {
                call.Items[typeof(IServiceProvider)] = http.RequestServices;
                call.User ??= http.User;
                return await next(call).ConfigureAwait(false);
            }

            await using var scope = root.CreateAsyncScope();
            call.Items[typeof(IServiceProvider)] = scope.ServiceProvider;
            return await next(call).ConfigureAwait(false);
        };
    }

    public static class ChrysalisServiceCollectionExtensions
    {
        /// <summary>Registers a <see cref="ChrysalisServer"/> singleton configured by <paramref name="configure"/>.</summary>
        public static ChrysalisBuilder AddChrysalis(this IServiceCollection services, Action<ChrysalisBuilder>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(services);
            var builder = new ChrysalisBuilder(services);
            configure?.Invoke(builder);
            services.TryAddSingleton(root => builder.Build(root));
            return builder;
        }

        /// <summary>
        /// Runs the Chrysalis HTTP server (HTTP/1.1, HTTP/2, TLS) as a hosted service next to (or instead of) Kestrel, with the
        /// protocols <paramref name="map"/> adds. Can be called more than once.
        /// </summary>
        public static IServiceCollection AddChrysalisHttpServer(this IServiceCollection services, Action<HttpServerOptions> configure, Action<HttpServer, ChrysalisServer> map)
        {
            ArgumentNullException.ThrowIfNull(configure);
            ArgumentNullException.ThrowIfNull(map);
            services.AddSingleton<IHostedService>(root =>
            {
                var options = new HttpServerOptions { Logger = root.GetService<ILoggerFactory>()?.CreateLogger("Butterfly.Chrysalis.Http") ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance };
                configure(options);
                var server = new HttpServer(options);
                map(server, root.GetRequiredService<ChrysalisServer>());
                return new ChrysalisHostedService(server.Start, server.StopAsync, server);
            });
            return services;
        }

        /// <summary>Runs the Chrysalis binary protocol server as a hosted service. Can be called more than once.</summary>
        public static IServiceCollection AddChrysalisBinaryServer(this IServiceCollection services, Action<ChrysalisBinaryServerOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);
            services.AddSingleton<IHostedService>(root =>
            {
                var options = new ChrysalisBinaryServerOptions();
                configure(options);
                var server = new ChrysalisBinaryServer(root.GetRequiredService<ChrysalisServer>(), options);
                return new ChrysalisHostedService(server.Start, server.StopAsync, server);
            });
            return services;
        }

        /// <summary>The services of the call: the request scope in ASP.NET Core, or the scope Chrysalis created for it.</summary>
        public static IServiceProvider GetServices(this ChrysalisCallContext call, IServiceProvider? fallback = null) =>
            call.Items.TryGetValue(typeof(IServiceProvider), out var services) && services is IServiceProvider provider
                ? provider
                : fallback ?? throw new InvalidOperationException("The call has no service provider: register the server with AddChrysalis.");
    }

    /// <summary>A Chrysalis server (HTTP or binary) started and stopped with the host.</summary>
    public sealed class ChrysalisHostedService : IHostedService
    {
        private readonly Action start;
        private readonly Func<Task> stop;

        internal ChrysalisHostedService(Action start, Func<Task> stop, object server)
        {
            this.start = start;
            this.stop = stop;
            Server = server;
        }

        /// <summary>The <see cref="HttpServer"/> or <see cref="ChrysalisBinaryServer"/> (its endpoints tell the real ports).</summary>
        public object Server { get; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            start();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => stop();
    }
}

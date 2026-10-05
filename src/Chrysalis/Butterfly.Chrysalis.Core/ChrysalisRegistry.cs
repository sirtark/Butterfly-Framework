using System.Collections.Concurrent;

namespace Butterfly.Chrysalis
{
    /// <summary>
    /// Descriptions of the services, filled by the code the source generator emits for every assembly that declares a
    /// <see cref="ChrysalisServiceAttribute"/> interface (a module initializer registers them when the assembly loads).
    /// </summary>
    public static class ChrysalisRegistry
    {
        private sealed record Entry(Lazy<ChrysalisService> Service, Func<IChrysalisInvoker, object>? CreateClient);

        private static readonly ConcurrentDictionary<Type, Entry> Services = new();

        /// <summary>Registers a service. Called by generated code; the first registration of a contract wins.</summary>
        public static void RegisterService(Type contract, Func<ChrysalisService> describe, Func<IChrysalisInvoker, object>? createClient = null)
        {
            ArgumentNullException.ThrowIfNull(contract);
            ArgumentNullException.ThrowIfNull(describe);
            Services.TryAdd(contract, new Entry(new Lazy<ChrysalisService>(describe), createClient));
        }

        public static bool IsRegistered(Type contract) => Services.ContainsKey(contract);

        public static ChrysalisService GetService<TContract>() where TContract : class => GetService(typeof(TContract));

        /// <exception cref="InvalidOperationException">The contract was not described by the generator.</exception>
        public static ChrysalisService GetService(Type contract) =>
            Find(contract).Service.Value;

        /// <summary>Creates the generated typed client of a service over an invoker (binary client, in-process...).</summary>
        public static TContract CreateClient<TContract>(IChrysalisInvoker invoker) where TContract : class
        {
            ArgumentNullException.ThrowIfNull(invoker);
            var entry = Find(typeof(TContract));
            return (TContract)(entry.CreateClient ?? throw new InvalidOperationException($"No client was generated for {typeof(TContract)}."))(invoker);
        }

        private static Entry Find(Type contract) =>
            Services.TryGetValue(contract, out var entry)
                ? entry
                : throw new InvalidOperationException(
                    $"{contract} is not a Chrysalis service. Mark it with [ChrysalisService] in a project that references Butterfly.Chrysalis.Core, so the generator describes it.");
    }
}

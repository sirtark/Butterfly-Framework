using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace Butterfly.Serialization
{
    /// <summary>
    /// Descriptions of the types that can be serialized. The source generator registers them for every assembly (contracts
    /// marked with <see cref="SerializationContractAttribute"/>, types used with <see cref="ButterflySerializer"/>, and
    /// types of Chrysalis services); scalars and dynamic types are always available.
    /// </summary>
    public static class SerializationRegistry
    {
        private static readonly ConcurrentDictionary<Type, Lazy<SerializableType>> Types = new();

        /// <summary>Registers a description. Called by generated code; the first registration of a type wins.</summary>
        public static void Register(Type clrType, Func<SerializableType> describe)
        {
            ArgumentNullException.ThrowIfNull(clrType);
            ArgumentNullException.ThrowIfNull(describe);
            Types.TryAdd(clrType, new Lazy<SerializableType>(describe));
        }

        public static SerializableType Get<T>() => Get(typeof(T));

        /// <exception cref="InvalidOperationException">The type was not described by the generator.</exception>
        public static SerializableType Get(Type clrType) =>
            TryGet(clrType, out var type)
                ? type
                : throw new InvalidOperationException(
                    $"{clrType} is not described. Mark it with [SerializationContract], or serialize it through ButterflySerializer.Serialize<T>/Deserialize<T> in a project that references Butterfly.Serialization.Core, so the generator describes it.");

        public static bool TryGet(Type clrType, [NotNullWhen(true)] out SerializableType? type)
        {
            // Nullable<T> serializes as T (or null).
            var underlying = Nullable.GetUnderlyingType(clrType) ?? clrType;
            type = SerializableType.ForScalar(underlying) ?? DynamicType.For(underlying);
            if (type is not null)
                return true;
            if (Types.TryGetValue(underlying, out var lazy))
            {
                type = lazy.Value;
                return true;
            }
            return false;
        }

        public static bool IsRegistered(Type clrType) => TryGet(clrType, out _);
    }
}

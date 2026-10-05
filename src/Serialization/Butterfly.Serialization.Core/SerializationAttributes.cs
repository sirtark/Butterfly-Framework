namespace Butterfly.Serialization
{
    public enum SerializationMode : byte
    {
        /// <summary>Every public property is serialized unless marked with <see cref="DontSerializeAttribute"/>.</summary>
        OptOut,
        /// <summary>Only properties marked with <see cref="SerializeAttribute"/> are serialized.</summary>
        OptIn
    }

    /// <summary>
    /// Marks a class, record or struct as a serialization contract, so the source generator describes it, and sets how its
    /// members are chosen. Types used by other contracts or by Chrysalis services are described without it (opt-out).
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
    public sealed class SerializationContractAttribute : Attribute
    {
        public SerializationMode Mode { get; set; } = SerializationMode.OptOut;
        /// <summary>Name in schemas (WSDL, .proto) and XML root elements. Default: the type name.</summary>
        public string? Name { get; set; }
    }

    /// <summary>
    /// Includes a property (required in <see cref="SerializationMode.OptIn"/> contracts), and optionally renames it, gives it
    /// a stable field number for binary formats, or restricts it to some profiles.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, Inherited = false)]
    public sealed class SerializeAttribute : Attribute
    {
        public SerializeAttribute()
        { }
        public SerializeAttribute(string name)
        {
            Name = name;
        }

        /// <summary>Name on the wire, used as is (the naming policy of the profile does not apply).</summary>
        public string? Name { get; set; }
        /// <summary>
        /// Field number in Protocol Buffers. Without it members are numbered in declaration order, so reordering them
        /// breaks compatibility: give explicit numbers to contracts that evolve.
        /// </summary>
        public int Number { get; set; }
        /// <summary>Profiles that include the property; empty means every profile.</summary>
        public string[] Profiles { get; set; } = [];
    }

    /// <summary>Leaves a property out: always, or only in the given profiles.</summary>
    [AttributeUsage(AttributeTargets.Property, Inherited = false)]
    public sealed class DontSerializeAttribute : Attribute
    {
        public DontSerializeAttribute()
        { }
        public DontSerializeAttribute(params string[] profiles)
        {
            Profiles = profiles;
        }

        /// <summary>Profiles that leave the property out; empty means every profile (the property is not part of the contract).</summary>
        public string[] Profiles { get; set; } = [];
    }

    /// <summary>Renames an enum member on the wire (text formats).</summary>
    [AttributeUsage(AttributeTargets.Field, Inherited = false)]
    public sealed class EnumNameAttribute(string name) : Attribute
    {
        public string Name { get; } = name;
    }
}

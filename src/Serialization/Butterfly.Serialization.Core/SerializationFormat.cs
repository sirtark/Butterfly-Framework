using System.Buffers;
using System.Text;

namespace Butterfly.Serialization
{
    /// <summary>A wire format: turns described values into bytes and back.</summary>
    public abstract class SerializationFormat
    {
        /// <summary>Short name: "json", "xml", "protobuf"...</summary>
        public abstract string Name { get; }
        /// <summary>Media type for HTTP content negotiation.</summary>
        public abstract string MediaType { get; }
        /// <summary>Whether the output is text (UTF-8).</summary>
        public abstract bool IsText { get; }

        public abstract void Write(IBufferWriter<byte> output, SerializableType type, object? value, SerializationProfile profile);

        /// <exception cref="SerializationException">The input is malformed or does not match the type.</exception>
        public abstract object? Read(ReadOnlyMemory<byte> input, SerializableType type, SerializationProfile profile);

        public byte[] Serialize(SerializableType type, object? value, SerializationProfile? profile = null)
        {
            var output = new ArrayBufferWriter<byte>();
            Write(output, type, value, profile ?? SerializationProfile.Default);
            return output.WrittenSpan.ToArray();
        }

        public string SerializeToString(SerializableType type, object? value, SerializationProfile? profile = null) =>
            IsText ? Encoding.UTF8.GetString(Serialize(type, value, profile)) : throw new InvalidOperationException($"The {Name} format is binary; use Serialize.");

        public object? Deserialize(SerializableType type, ReadOnlyMemory<byte> input, SerializationProfile? profile = null) =>
            Read(input, type, profile ?? SerializationProfile.Default);

        public object? Deserialize(SerializableType type, string text, SerializationProfile? profile = null) =>
            Read(Encoding.UTF8.GetBytes(text), type, profile ?? SerializationProfile.Default);

        public override string ToString() => Name;
    }

    /// <summary>
    /// A format that only knows its syntax: it writes and reads <see cref="SerializationValue"/> trees, and the typed side
    /// (profiles, naming, enums, defaults) is handled by <see cref="ValueConverter"/>. Schema-less reads and writes are
    /// available through <see cref="WriteValue"/> and <see cref="ReadValue"/>.
    /// </summary>
    public abstract class ValueFormat : SerializationFormat
    {
        public abstract ValueConventions Conventions { get; }

        public abstract void WriteValue(IBufferWriter<byte> output, SerializationValue value, SerializationProfile profile);

        /// <exception cref="SerializationException">The input is malformed.</exception>
        public abstract SerializationValue ReadValue(ReadOnlyMemory<byte> input, SerializationProfile profile);

        public sealed override void Write(IBufferWriter<byte> output, SerializableType type, object? value, SerializationProfile profile) =>
            WriteValue(output, ValueConverter.ToValue(type, value, Conventions, profile), profile);

        public sealed override object? Read(ReadOnlyMemory<byte> input, SerializableType type, SerializationProfile profile) =>
            ValueConverter.FromValue(type, ReadValue(input, profile), Conventions, profile);
    }
}

namespace Butterfly.Serialization
{
    /// <summary>
    /// Entry point: <c>ButterflySerializer.Serialize(order, JsonFormat.Instance, profile)</c>. The source generator sees
    /// every call and describes the type argument, so any class, record or collection used here works without attributes.
    /// </summary>
    public static class ButterflySerializer
    {
        public static byte[] Serialize<T>(T value, SerializationFormat format, SerializationProfile? profile = null)
        {
            ArgumentNullException.ThrowIfNull(format);
            return format.Serialize(SerializationRegistry.Get<T>(), value, profile);
        }

        /// <exception cref="InvalidOperationException">The format is binary.</exception>
        public static string SerializeToString<T>(T value, SerializationFormat format, SerializationProfile? profile = null)
        {
            ArgumentNullException.ThrowIfNull(format);
            return format.SerializeToString(SerializationRegistry.Get<T>(), value, profile);
        }

        /// <exception cref="SerializationException">The input is malformed or does not match <typeparamref name="T"/>.</exception>
        public static T Deserialize<T>(ReadOnlyMemory<byte> input, SerializationFormat format, SerializationProfile? profile = null)
        {
            ArgumentNullException.ThrowIfNull(format);
            return (T)format.Deserialize(SerializationRegistry.Get<T>(), input, profile)!;
        }

        public static T Deserialize<T>(byte[] input, SerializationFormat format, SerializationProfile? profile = null) =>
            Deserialize<T>(input.AsMemory(), format, profile);

        public static T Deserialize<T>(string text, SerializationFormat format, SerializationProfile? profile = null)
        {
            ArgumentNullException.ThrowIfNull(format);
            return (T)format.Deserialize(SerializationRegistry.Get<T>(), text, profile)!;
        }
    }
}

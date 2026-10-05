namespace Butterfly.Serialization
{
    /// <summary>The input does not match the contract, or a value cannot be written. <see cref="Path"/> names the bad value ("order.items[2].price").</summary>
    public sealed class SerializationException : Exception
    {
        public SerializationException(string path, string problem, Exception? innerException = null)
            : base(path.Length == 0 ? problem : $"'{path}': {problem}", innerException)
        {
            Path = path;
            Problem = problem;
        }

        public string Path { get; }
        /// <summary>The message without the path.</summary>
        public string Problem { get; }

        public static string Child(string path, string name) => path.Length == 0 ? name : path + "." + name;
        public static string Item(string path, int index) => $"{path}[{index}]";
    }
}

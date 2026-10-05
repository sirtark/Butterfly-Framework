namespace Butterfly.Tool.Cli
{
    public interface ICommand
    {
        string Name { get; }
        string Description { get; }

        /// <summary>Positional arguments as shown in the usage line, e.g. "&lt;package&gt;...".</summary>
        string Arguments { get; }

        IReadOnlyList<CommandOption> Options { get; }

        int Execute(ParsedArguments arguments);
    }

    public sealed record CommandOption(string Name, string? Alias, string Description, string? ValueName = null)
    {
        public bool HasValue => ValueName is not null;

        public bool Matches(string token) => token == Name || (Alias is not null && token == Alias);
    }

    /// <summary>A user facing error: printed without a stack trace.</summary>
    public sealed class CliException(string message) : Exception(message);
}

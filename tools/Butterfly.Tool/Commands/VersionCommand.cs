using Butterfly.Tool.Cli;

namespace Butterfly.Tool.Commands
{
    public sealed class VersionCommand : ICommand
    {
        public string Name => "version";
        public string Description => "Print the framework version this tool hands out.";
        public string Arguments => "";
        public IReadOnlyList<CommandOption> Options { get; } = [];

        public int Execute(ParsedArguments arguments)
        {
            Output.Info(ButterflyFramework.Version);
            return 0;
        }
    }
}

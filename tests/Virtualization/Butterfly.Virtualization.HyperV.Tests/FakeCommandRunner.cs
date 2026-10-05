using Butterfly.Virtualization.Native;
using System.Text;

namespace Butterfly.Virtualization.HyperV.Tests
{
    // Answers every PowerShell invocation with a fixed result and keeps the decoded scripts.
    internal sealed class FakeCommandRunner(CommandResult? result) : ICommandRunner
    {
        public List<string> Scripts { get; } = [];

        public static FakeCommandRunner Returning(string output, int exitCode = 0, string error = "") => new(new CommandResult(exitCode, output, error));

        public Task<CommandResult?> RunAsync(string fileName, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? environment, CancellationToken cancellationToken)
        {
            Assert.Equal("powershell.exe", fileName);
            Assert.Equal(["-NoProfile", "-NonInteractive", "-EncodedCommand"], arguments.Take(3));
            Scripts.Add(Encoding.Unicode.GetString(Convert.FromBase64String(arguments[3])));
            return Task.FromResult(result);
        }
    }
}

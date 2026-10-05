using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Butterfly.Virtualization.Native
{
    internal sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError);

    // Seam between the providers and the processes they run, so the commands they build can be tested without a hypervisor.
    internal interface ICommandRunner
    {
        /// <param name="environment">Variables added to the inherited environment of the process.</param>
        /// <returns>The result, or null when the program does not exist on this machine.</returns>
        Task<CommandResult?> RunAsync(string fileName, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? environment, CancellationToken cancellationToken);
    }

    internal sealed class ProcessCommandRunner : ICommandRunner
    {
        public static ProcessCommandRunner Instance { get; } = new();

        public async Task<CommandResult?> RunAsync(string fileName, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? environment, CancellationToken cancellationToken)
        {
            var startInfo = new ProcessStartInfo(fileName)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            // ArgumentList quotes each argument: nothing a caller passes is interpreted by a shell.
            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);
            if (environment is not null)
            {
                foreach (var (name, value) in environment)
                    startInfo.Environment[name] = value;
            }

            using var process = new Process { StartInfo = startInfo };
            try
            {
                process.Start();
            }
            catch (Win32Exception)
            {
                return null;
            }

            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                throw;
            }

            return new CommandResult(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
        }
    }
}

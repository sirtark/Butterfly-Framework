using System.Diagnostics;
using System.Text.Json;

using Butterfly.Tool.Cli;

namespace Butterfly.Tool.Projects
{
    /// <summary>What MSBuild actually evaluates for a project (after SDKs, Directory.*.props and global.json).</summary>
    public sealed record ProjectEvaluation(
        string ProjectPath,
        bool IsButterflyFrameworkProject,
        bool UsesButterflySdk,
        string? ButterflyVersion,
        bool ManagesPackageVersionsCentrally,
        string? CentralPackagesPath)
    {
        /// <summary>Framework projects build from source; Butterfly packages and Butterfly.Sdk are for consumers only.</summary>
        public void EnsureConsumer()
        {
            if (IsButterflyFrameworkProject)
                throw new CliException(
                    $"'{Path.GetFileName(ProjectPath)}' is part of the Butterfly framework: it must keep Microsoft.NET.Sdk " +
                    "and use ProjectReference for other Butterfly projects.");
        }
    }

    public static class DotNet
    {
        // Set by the dotnet host when it runs a .NET tool; falls back to PATH.
        private static string Executable => Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet";

        public static ProjectEvaluation Evaluate(string projectPath)
        {
            (int exitCode, string output, string error) = Run("msbuild", projectPath, "-nologo",
                "-getProperty:IsButterflyFrameworkProject",
                "-getProperty:UsingButterflySdk",
                "-getProperty:ButterflyVersion",
                "-getProperty:ManagePackageVersionsCentrally",
                "-getProperty:DirectoryPackagesPropsPath");

            if (exitCode != 0)
                throw new CliException($"MSBuild could not evaluate '{projectPath}':{Environment.NewLine}{(output + error).Trim()}");

            JsonElement properties = JsonDocument.Parse(output).RootElement.GetProperty("Properties");
            string? Get(string name) => properties.GetProperty(name).GetString() is { Length: > 0 } value ? value : null;

            return new ProjectEvaluation(
                ProjectPath: projectPath,
                IsButterflyFrameworkProject: Get("IsButterflyFrameworkProject") == "true",
                UsesButterflySdk: Get("UsingButterflySdk") == "true",
                ButterflyVersion: Get("ButterflyVersion"),
                ManagesPackageVersionsCentrally: Get("ManagePackageVersionsCentrally")?.Equals("true", StringComparison.OrdinalIgnoreCase) == true,
                CentralPackagesPath: Get("DirectoryPackagesPropsPath"));
        }

        public static void Restore(string projectPath)
        {
            Output.Info("Restoring packages...");
            (int exitCode, string output, string error) = Run("restore", projectPath, "-nologo", "-v:q");
            if (exitCode != 0)
                throw new CliException($"Restore failed:{Environment.NewLine}{(output + error).Trim()}");
        }

        private static (int ExitCode, string Output, string Error) Run(params string[] arguments)
        {
            ProcessStartInfo startInfo = new(Executable, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            startInfo.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";

            using Process process = Process.Start(startInfo) ?? throw new CliException("Could not start 'dotnet'.");
            Task<string> error = process.StandardError.ReadToEndAsync();
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            return (process.ExitCode, output, error.Result);
        }
    }
}

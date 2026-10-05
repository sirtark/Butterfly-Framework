using Butterfly.Tool.Cli;
using Butterfly.Tool.Projects;

namespace Butterfly.Tool.Commands
{
    /// <summary>
    /// Moves a project to Butterfly.Sdk at the framework version and lets every Butterfly.* reference follow it.
    /// Running it again after updating the tool upgrades the project to the new framework version.
    /// </summary>
    public sealed class InitCommand : ICommand
    {
        public string Name => "init";
        public string Description => $"Use Butterfly.Sdk {ButterflyFramework.Version} in a project (also upgrades it).";
        public string Arguments => "";
        public IReadOnlyList<CommandOption> Options { get; } = [ProjectLocator.ProjectOption, ProjectTransaction.NoRestoreOption];

        public int Execute(ParsedArguments arguments)
        {
            string projectPath = ProjectLocator.Locate(arguments);
            DotNet.Evaluate(projectPath).EnsureConsumer();
            ProjectFile project = ProjectFile.Load(projectPath);

            Output.Info(project.UseButterflySdk(ButterflyFramework.Version)
                ?? $"The project already uses {ButterflyFramework.SdkName} {ButterflyFramework.Version}.");

            foreach (var reference in project.PackageReferences.ToList())
            {
                string id = (string)reference.Attribute("Include")!;
                if (ButterflyFramework.IsButterflyPackage(id) && ProjectFile.ClearVersion(reference))
                    Output.Info($"{id} now follows the framework version.");
            }

            ProjectTransaction.Commit(projectPath, arguments, project);
            return 0;
        }
    }
}

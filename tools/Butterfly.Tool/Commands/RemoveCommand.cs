using Butterfly.Tool.Cli;
using Butterfly.Tool.Projects;

namespace Butterfly.Tool.Commands
{
    public sealed class RemoveCommand : ICommand
    {
        public string Name => "remove";
        public string Description => "Remove Butterfly packages from a project.";
        public string Arguments => "<package>...";
        public IReadOnlyList<CommandOption> Options { get; } = [ProjectLocator.ProjectOption, ProjectTransaction.NoRestoreOption];

        public int Execute(ParsedArguments arguments)
        {
            if (arguments.Positionals.Count == 0)
                throw new CliException("Specify at least one package, e.g. 'butterfly remove Networking.Sockets'.");

            string projectPath = ProjectLocator.Locate(arguments);
            ProjectFile project = ProjectFile.Load(projectPath);
            bool changed = false;

            foreach (string name in arguments.Positionals)
            {
                // Removing works even for packages that are no longer part of the catalog.
                string id = ButterflyFramework.Find(name)?.Id
                    ?? (ButterflyFramework.IsButterflyPackage(name) ? name : ButterflyFramework.PackagePrefix + name);

                if (project.RemovePackageReference(id))
                {
                    Output.Info($"Removed {id}");
                    changed = true;
                }
                else
                {
                    Output.Warning($"{id} is not referenced by the project.");
                }
            }

            if (changed)
                ProjectTransaction.Commit(projectPath, arguments, project);

            return 0;
        }
    }
}

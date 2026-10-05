using Butterfly.Tool.Cli;
using Butterfly.Tool.Projects;

namespace Butterfly.Tool.Commands
{
    public sealed class ListCommand : ICommand
    {
        public string Name => "list";
        public string Description => "List the packages of the framework; '*' marks the ones the project references.";
        public string Arguments => "";
        public IReadOnlyList<CommandOption> Options { get; } = [ProjectLocator.ProjectOption];

        public int Execute(ParsedArguments arguments)
        {
            ProjectFile? project = TryLoadProject(arguments);

            Output.Info($"Butterfly {ButterflyFramework.Version}" + (project is null ? "" : $" ({Path.GetFileName(project.Path)})"));
            Output.Info("");

            int width = ButterflyFramework.Packages.Max(p => p.Id.Length) + 2;
            foreach (ButterflyPackage package in ButterflyFramework.Packages)
            {
                string mark = project?.FindPackageReference(package.Id) is null ? " " : "*";
                Output.Info($"{mark} {package.Id.PadRight(width)}{package.Description}");
            }

            return 0;
        }

        private static ProjectFile? TryLoadProject(ParsedArguments arguments)
        {
            // An explicit --project must exist; the current folder is only inspected if it holds a project.
            if (arguments.Has(ProjectLocator.ProjectOption))
                return ProjectFile.Load(ProjectLocator.Locate(arguments));

            try
            {
                return ProjectFile.Load(ProjectLocator.Locate(path: null));
            }
            catch (CliException)
            {
                return null;
            }
        }
    }
}

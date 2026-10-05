using System.Xml.Linq;

using Butterfly.Tool.Cli;
using Butterfly.Tool.Projects;

namespace Butterfly.Tool.Commands
{
    /// <summary>
    /// Adds Butterfly packages so they always resolve to the framework version:
    ///   - projects using Butterfly.Sdk get a versionless reference (the SDK supplies the version);
    ///   - projects with central package management get the version in Directory.Packages.props;
    ///   - any other project gets Version="&lt;framework version&gt;" on the reference.
    /// </summary>
    public sealed class AddCommand : ICommand
    {
        public string Name => "add";
        public string Description => $"Add Butterfly packages to a project at the framework version ({ButterflyFramework.Version}).";
        public string Arguments => "<package>...";
        public IReadOnlyList<CommandOption> Options { get; } = [ProjectLocator.ProjectOption, ProjectTransaction.NoRestoreOption];

        public int Execute(ParsedArguments arguments)
        {
            if (arguments.Positionals.Count == 0)
                throw new CliException("Specify at least one package, e.g. 'butterfly add Networking.Sockets'.");

            ButterflyPackage[] packages = [.. arguments.Positionals.Select(ButterflyFramework.Resolve).DistinctBy(p => p.Id)];

            string projectPath = ProjectLocator.Locate(arguments);
            ProjectEvaluation evaluation = DotNet.Evaluate(projectPath);
            evaluation.EnsureConsumer();
            ProjectFile project = ProjectFile.Load(projectPath);
            CentralPackagesFile? central = null;

            string version = ButterflyFramework.Version;
            if (evaluation.UsesButterflySdk)
            {
                version = evaluation.ButterflyVersion ?? version;
                if (version != ButterflyFramework.Version)
                    Output.Warning($"The project uses Butterfly.Sdk {version}, so the packages will resolve to {version}. " +
                                   $"Run 'butterfly init' to move it to {ButterflyFramework.Version}.");
            }
            else if (evaluation.ManagesPackageVersionsCentrally)
            {
                central = CentralPackagesFile.Load(evaluation.CentralPackagesPath
                    ?? throw new CliException("The project uses central package management but Directory.Packages.props was not found."));
            }

            foreach (ButterflyPackage package in packages)
            {
                XElement? reference = project.FindPackageReference(package.Id);
                bool existed = reference is not null;
                reference ??= project.AddPackageReference(package.Id);

                if (evaluation.UsesButterflySdk)
                {
                    ProjectFile.ClearVersion(reference);
                }
                else if (central is not null)
                {
                    ProjectFile.ClearVersion(reference);
                    central.SetPackageVersion(package.Id, version);
                }
                else
                {
                    ProjectFile.RemoveMetadata(reference, "VersionOverride");
                    ProjectFile.SetMetadata(reference, "Version", version);
                }

                Output.Info($"{(existed ? "Updated" : "Added")} {package.Id} {version}");
            }

            ProjectTransaction.Commit(projectPath, arguments, project, central);
            return 0;
        }
    }
}

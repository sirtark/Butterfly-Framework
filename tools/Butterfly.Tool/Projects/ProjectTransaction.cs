using Butterfly.Tool.Cli;

namespace Butterfly.Tool.Projects
{
    /// <summary>Saves a set of edited files and restores the project; everything is reverted if the restore fails.</summary>
    public static class ProjectTransaction
    {
        public static readonly CommandOption NoRestoreOption =
            new("--no-restore", null, "Only edit the files, do not run 'dotnet restore'.");

        public static void Commit(string projectPath, ParsedArguments arguments, params XmlFile?[] files)
        {
            XmlFile[] changed = [.. files.OfType<XmlFile>()];

            foreach (XmlFile file in changed)
                file.Save();

            if (arguments.Has(NoRestoreOption))
                return;

            try
            {
                DotNet.Restore(projectPath);
            }
            catch (CliException)
            {
                foreach (XmlFile file in changed)
                    file.Revert();

                Output.Warning("The changes were reverted.");
                throw;
            }
        }
    }
}

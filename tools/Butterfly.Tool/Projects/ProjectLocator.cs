using Butterfly.Tool.Cli;

namespace Butterfly.Tool.Projects
{
    public static class ProjectLocator
    {
        public static readonly CommandOption ProjectOption =
            new("--project", "-p", "Project file or folder. Defaults to the current folder.", "path");

        private static readonly string[] s_projectExtensions = [".csproj", ".fsproj", ".vbproj"];

        public static string Locate(ParsedArguments arguments) => Locate(arguments.Get(ProjectOption));

        public static string Locate(string? path)
        {
            path = Path.GetFullPath(path ?? Directory.GetCurrentDirectory());

            if (File.Exists(path))
            {
                if (!s_projectExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                    throw new CliException($"'{path}' is not a project file.");
                return path;
            }

            if (!Directory.Exists(path))
                throw new CliException($"'{path}' does not exist.");

            string[] projects = [.. Directory.EnumerateFiles(path, "*.*proj")
                .Where(p => s_projectExtensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))];

            return projects.Length switch
            {
                1 => projects[0],
                0 => throw new CliException($"No project found in '{path}'. Use --project to choose one."),
                _ => throw new CliException($"Several projects found in '{path}'. Use --project to choose one."),
            };
        }
    }
}

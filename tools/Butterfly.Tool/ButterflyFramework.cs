using System.Reflection;

namespace Butterfly.Tool
{
    public sealed record ButterflyPackage(string Id, string Description);

    /// <summary>The Butterfly release this tool belongs to (embedded at build time, see Butterfly.Tool.csproj).</summary>
    public static class ButterflyFramework
    {
        public const string PackagePrefix = "Butterfly.";
        public const string SdkName = "Butterfly.Sdk";

        private static readonly AssemblyMetadataAttribute[] s_metadata =
            [.. typeof(ButterflyFramework).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()];

        public static string Version { get; } =
            s_metadata.First(m => m.Key == "ButterflyVersion").Value!;

        public static IReadOnlyList<ButterflyPackage> Packages { get; } =
        [
            .. s_metadata
                .Where(m => m.Key == "ButterflyPackage" && !string.IsNullOrEmpty(m.Value))
                .Select(m => m.Value!.Split('|', 2))
                .Select(parts => new ButterflyPackage(parts[0], parts.Length > 1 ? parts[1] : ""))
                .OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
        ];

        public static bool IsButterflyPackage(string id) =>
            id.StartsWith(PackagePrefix, StringComparison.OrdinalIgnoreCase);

        /// <summary>Accepts both the full id ("Butterfly.Networking.Sockets") and the short one ("Networking.Sockets").</summary>
        public static ButterflyPackage? Find(string name)
        {
            string id = IsButterflyPackage(name) ? name : PackagePrefix + name;
            return Packages.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        }

        public static ButterflyPackage Resolve(string name) =>
            Find(name) ?? throw new Cli.CliException(
                $"'{name}' is not a Butterfly {Version} package. Run 'butterfly list' to see the available packages.");
    }
}

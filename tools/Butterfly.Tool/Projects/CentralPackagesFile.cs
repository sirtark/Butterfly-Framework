using System.Xml.Linq;

namespace Butterfly.Tool.Projects
{
    /// <summary>A Directory.Packages.props file (NuGet central package management).</summary>
    public sealed class CentralPackagesFile : XmlFile
    {
        private CentralPackagesFile(string path) : base(path) { }

        public static CentralPackagesFile Load(string path) => new(path);

        /// <summary>Adds or updates <c>&lt;PackageVersion Include="id" Version="version" /&gt;</c>. Returns false if it was already set.</summary>
        public bool SetPackageVersion(string id, string version)
        {
            XElement? entry = Root.Descendants(Name("PackageVersion"))
                .FirstOrDefault(e => string.Equals((string?)e.Attribute("Include"), id, StringComparison.OrdinalIgnoreCase));

            if (entry is not null)
            {
                string? current = (string?)entry.Attribute("Version") ?? entry.Element(Name("Version"))?.Value;
                if (current == version)
                    return false;

                ProjectFile.SetMetadata(entry, "Version", version);
                return true;
            }

            XElement? group = Root.Elements(Name("ItemGroup"))
                .FirstOrDefault(g => g.Attribute("Condition") is null && g.Elements(Name("PackageVersion")).Any());

            if (group is null)
            {
                group = new XElement(Name("ItemGroup"));
                AppendChild(Root, group, blankLineBefore: true);
            }

            AppendChild(group, new XElement(Name("PackageVersion"), new XAttribute("Include", id), new XAttribute("Version", version)));
            return true;
        }
    }
}

using System.Xml.Linq;

using Butterfly.Tool.Cli;

namespace Butterfly.Tool.Projects
{
    public sealed class ProjectFile : XmlFile
    {
        private const string DefaultSdk = "Microsoft.NET.Sdk";

        private ProjectFile(string path) : base(path)
        {
            if (Root.Name.LocalName != "Project")
                throw new CliException($"'{Path}' is not an MSBuild project.");
        }

        public static ProjectFile Load(string path) => new(path);

        public IEnumerable<XElement> PackageReferences =>
            Root.Descendants(Name("PackageReference")).Where(e => e.Attribute("Include") is not null);

        public XElement? FindPackageReference(string id) =>
            PackageReferences.FirstOrDefault(e => string.Equals((string?)e.Attribute("Include"), id, StringComparison.OrdinalIgnoreCase));

        public XElement AddPackageReference(string id)
        {
            XElement reference = new(Name("PackageReference"), new XAttribute("Include", id));

            // Join the first unconditional ItemGroup that already holds package references, or start a new one.
            XElement? group = Root.Elements(Name("ItemGroup"))
                .FirstOrDefault(g => g.Attribute("Condition") is null && g.Elements(Name("PackageReference")).Any());

            if (group is null)
            {
                group = new XElement(Name("ItemGroup"));
                AppendChild(Root, group, blankLineBefore: true);
            }

            AppendChild(group, reference);
            return reference;
        }

        public bool RemovePackageReference(string id)
        {
            XElement? reference = FindPackageReference(id);
            if (reference is null)
                return false;

            XElement? group = reference.Parent;
            RemoveElement(reference);

            if (group is not null && group.Name == Name("ItemGroup") && !group.Elements().Any())
                RemoveElement(group);

            return true;
        }

        /// <summary>Makes the reference follow the version supplied by Butterfly.Sdk.</summary>
        public static bool ClearVersion(XElement reference) =>
            RemoveMetadata(reference, "Version") | RemoveMetadata(reference, "VersionOverride");

        public static void SetMetadata(XElement item, string name, string value)
        {
            XElement? element = item.Elements(item.Name.Namespace + name).FirstOrDefault();
            if (element is not null)
                element.Value = value;
            else
                item.SetAttributeValue(name, value);
        }

        public static bool RemoveMetadata(XElement item, string name)
        {
            bool removed = false;

            if (item.Attribute(name) is XAttribute attribute)
            {
                attribute.Remove();
                removed = true;
            }

            foreach (XElement element in item.Elements(item.Name.Namespace + name).ToList())
            {
                RemoveElement(element);
                removed = true;
            }

            // <PackageReference Include="x"></PackageReference> left without metadata collapses to <PackageReference Include="x" />.
            if (removed && !item.Elements().Any())
                item.RemoveNodes();

            return removed;
        }

        /// <summary>
        /// Points the project at Butterfly.Sdk <paramref name="version"/>. Returns a description of the change,
        /// or null when the project already used that version.
        /// </summary>
        public string? UseButterflySdk(string version)
        {
            string target = $"{ButterflyFramework.SdkName}/{version}";

            // 1. <Project Sdk="...;Butterfly.Sdk[/x.y.z];...">
            XAttribute? sdkAttribute = Root.Attribute("Sdk");
            List<string> sdks = sdkAttribute is null
                ? []
                : [.. sdkAttribute.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

            int index = sdks.FindIndex(s => SdkName(s).Equals(ButterflyFramework.SdkName, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                if (sdks[index] == target)
                    return null;

                string previous = sdks[index];
                sdks[index] = target;
                sdkAttribute!.Value = string.Join(';', sdks);
                return $"Updated SDK '{previous}' to '{target}'.";
            }

            // 2. <Sdk Name="Butterfly.Sdk" Version="x.y.z" />
            XElement? sdkElement = Root.Elements(Name("Sdk"))
                .FirstOrDefault(e => string.Equals((string?)e.Attribute("Name"), ButterflyFramework.SdkName, StringComparison.OrdinalIgnoreCase));
            if (sdkElement is not null)
            {
                if ((string?)sdkElement.Attribute("Version") == version)
                    return null;

                sdkElement.SetAttributeValue("Version", version);
                return $"Updated SDK '{ButterflyFramework.SdkName}' to version {version}.";
            }

            // 3. Plain Microsoft.NET.Sdk: Butterfly.Sdk wraps it.
            if (sdks.Count == 1 && sdks[0].Equals(DefaultSdk, StringComparison.OrdinalIgnoreCase))
            {
                sdkAttribute!.Value = target;
                return $"Replaced SDK '{DefaultSdk}' with '{target}'.";
            }

            // 4. Any other SDK (Web, Worker, Razor...): Butterfly.Sdk is added on top of it.
            if (sdks.Count > 0)
            {
                XElement element = new(Name("Sdk"), new XAttribute("Name", ButterflyFramework.SdkName), new XAttribute("Version", version));
                XElement? first = Root.Elements().FirstOrDefault();
                if (first is null)
                    AppendChild(Root, element);
                else
                    first.AddBeforeSelf(element, new XText(first.PreviousNode is XText t ? t.Value : "\n"));

                return $"Added '<Sdk Name=\"{ButterflyFramework.SdkName}\" Version=\"{version}\" />' on top of '{sdkAttribute!.Value}'.";
            }

            throw new CliException($"'{Path}' does not use an SDK-style project; Butterfly.Sdk requires one.");
        }

        private static string SdkName(string sdk) => sdk.Split('/', 2)[0].Trim();
    }
}

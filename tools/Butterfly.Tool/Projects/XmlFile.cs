using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Butterfly.Tool.Projects
{
    /// <summary>
    /// An MSBuild XML file edited in place: whitespace, encoding (BOM), line endings and the XML declaration
    /// are preserved, and new elements follow the indentation already used by the file.
    /// </summary>
    public class XmlFile
    {
        private readonly bool _hasBom;
        private readonly string _newLine;
        private readonly bool _endsWithNewLine;

        protected XmlFile(string path)
        {
            Path = System.IO.Path.GetFullPath(path);
            OriginalContent = File.ReadAllBytes(Path);

            string text = new UTF8Encoding(false).GetString(OriginalContent);
            _hasBom = OriginalContent.AsSpan().StartsWith(Encoding.UTF8.Preamble);
            _newLine = text.Contains("\r\n") ? "\r\n" : "\n";
            _endsWithNewLine = text.EndsWith('\n');

            using MemoryStream stream = new(OriginalContent);
            Document = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
        }

        public string Path { get; }

        public byte[] OriginalContent { get; }

        public XDocument Document { get; }

        public XElement Root => Document.Root!;

        public XName Name(string localName) => Root.Name.Namespace + localName;

        public void Save()
        {
            XmlWriterSettings settings = new()
            {
                Encoding = new UTF8Encoding(_hasBom),
                OmitXmlDeclaration = Document.Declaration is null,
                NewLineHandling = NewLineHandling.Replace,
                NewLineChars = _newLine,
            };

            using MemoryStream stream = new();
            using (XmlWriter writer = XmlWriter.Create(stream, settings))
                Document.Save(writer);

            // Whitespace after the root element is not always kept by XDocument: restore the final new line if needed.
            byte[] content = stream.ToArray();
            if (_endsWithNewLine && (content.Length == 0 || content[^1] != (byte)'\n'))
                content = [.. content, .. settings.Encoding.GetBytes(_newLine)];

            File.WriteAllBytes(Path, content);
        }

        public void Revert() => File.WriteAllBytes(Path, OriginalContent);

        /// <summary>Appends <paramref name="child"/> as the last element of <paramref name="parent"/>, indented like its siblings.</summary>
        public static void AppendChild(XElement parent, XElement child, bool blankLineBefore = false)
        {
            string parentIndent = IndentOf(parent);
            XElement? last = parent.Elements().LastOrDefault();
            string separator = blankLineBefore ? "\n\n" : "\n";

            if (last is null)
            {
                string unit = IndentUnit(parent);
                parent.RemoveNodes();
                parent.Add(new XText("\n" + parentIndent + unit), child, new XText("\n" + parentIndent));
                return;
            }

            last.AddAfterSelf(new XText(separator + IndentOf(last)), child);
        }

        /// <summary>Removes an element together with the whitespace that precedes it.</summary>
        public static void RemoveElement(XElement element)
        {
            if (element.PreviousNode is XText text && string.IsNullOrWhiteSpace(text.Value))
                text.Remove();

            element.Remove();
        }

        private static string IndentOf(XElement element)
        {
            if (element.PreviousNode is not XText text)
                return "";

            int lineStart = text.Value.LastIndexOf('\n');
            return lineStart < 0 ? "" : text.Value[(lineStart + 1)..];
        }

        private static string IndentUnit(XElement element)
        {
            // Look for any parent/child pair in the document to learn the indentation step ("  ", "    ", "\t").
            foreach (XElement candidate in element.Document!.Descendants())
            {
                XElement? child = candidate.Elements().FirstOrDefault();
                if (child is null || candidate.Parent is null)
                    continue;

                string outer = IndentOf(candidate), inner = IndentOf(child);
                if (inner.Length > outer.Length && inner.StartsWith(outer))
                    return inner[outer.Length..];
            }

            return "  ";
        }
    }
}

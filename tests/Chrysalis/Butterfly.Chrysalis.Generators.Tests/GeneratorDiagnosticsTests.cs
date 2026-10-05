using Butterfly.Serialization;
using Butterfly.Serialization.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Butterfly.Chrysalis.Generators.Tests
{
    // Runs the generator on small programs, as the compiler does, and checks what it reports and produces.
    public class GeneratorDiagnosticsTests
    {
        private static (IReadOnlyList<Diagnostic> Generator, IReadOnlyList<Diagnostic> Compilation, string Source) Run(string code)
        {
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Select(path => MetadataReference.CreateFromFile(path))
                .Append(MetadataReference.CreateFromFile(typeof(ChrysalisServiceAttribute).Assembly.Location))
                .Append(MetadataReference.CreateFromFile(typeof(SerializableType).Assembly.Location));
            var compilation = CSharpCompilation.Create("Sample",
                [CSharpSyntaxTree.ParseText("using Butterfly.Chrysalis; using Butterfly.Serialization; using System.Threading; using System.Threading.Tasks; using System.Collections.Generic;\n" + code,
                    new CSharpParseOptions(LanguageVersion.Latest))],
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

            var driver = CSharpGeneratorDriver.Create(new SerializationGenerator()).RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
            var source = string.Concat(driver.GetRunResult().GeneratedTrees.Select(tree => tree.ToString()));
            return (diagnostics, output.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToList(), source);
        }

        [Fact]
        public void AValidServiceCompilesWithoutDiagnostics()
        {
            var (generator, compilation, source) = Run("""
                namespace Shop;
                public enum Size { Small, Large }
                public record Item(int Id, string Name, Size Size, IReadOnlyList<string> Tags);
                public class Page { public List<Item> Items { get; set; } = new(); public Page? Next { get; init; } public System.DateTime At { get; set; } public Dictionary<string, Item> ByName { get; set; } = new(); public object? Extra { get; set; } }
                [ChrysalisService]
                public interface IShop
                {
                    Task<Item> Get(int id, CancellationToken token);
                    ValueTask<Page> List(int? from);
                    void Ping();
                    Task Reset(ChrysalisCallContext context);
                    long Count();
                    ValueTask Touch(System.Guid id, byte[] data, decimal amount, System.DateTimeOffset when, double ratio, bool flag);
                    object? Run(System.Text.Json.Nodes.JsonNode? input, Dictionary<string, int?> options);
                }
                """);

            Assert.Empty(generator);
            Assert.Empty(compilation);
            Assert.Contains("sealed partial class ShopClient : global::Shop.IShop", source);
            Assert.Contains("ChrysalisRegistry.RegisterService(typeof(global::Shop.IShop)", source);
            Assert.Contains("SerializationRegistry.Register(typeof(global::Shop.Item)", source);
        }

        [Theory]
        [InlineData("Task<System.Uri> Get();", "CHRY001")]
        [InlineData("void Save(Dictionary<int, string> map);", "CHRY006")]
        [InlineData("void Save(List<List<int>> nested);", "CHRY001")]
        [InlineData("void Save(List<int?> numbers);", "CHRY001")]
        [InlineData("void Save(float value);", "CHRY001")]
        [InlineData("T Get<T>();", "CHRY002")]
        [InlineData("void Save(ref int value);", "CHRY002")]
        [InlineData("IAsyncEnumerable<int> Stream();", "CHRY002")]
        [InlineData("void Save(int a); void Save(string b);", "CHRY003")]
        [InlineData("void Save(NoConstructor value);", "CHRY004")]
        [InlineData("void Save(TwoNumbers value);", "CHRY005")]
        public void ReportsWhatCannotBeTransported(string members, string id)
        {
            var (generator, compilation, source) = Run($$"""
                public class NoConstructor { public NoConstructor(string other) { } public int Value { get; set; } }
                public class TwoNumbers { [Serialize(Number = 1)] public int A { get; set; } [Serialize(Number = 1)] public int B { get; set; } }
                [ChrysalisService]
                public interface IBroken { {{members}} }
                """);

            Assert.Contains(generator, diagnostic => diagnostic.Id == id && diagnostic.Severity == DiagnosticSeverity.Error);
            // The broken service is left out instead of producing code that does not compile.
            Assert.DoesNotContain("IBroken)", source);
            Assert.Empty(compilation);
        }

        [Fact]
        public void RenamesNumbersAndProfilesAreHonored()
        {
            var (generator, compilation, source) = Run("""
                public class Data
                {
                    [Serialize(Number = 10)] public int A { get; set; }
                    public int B { get; set; }
                    [Serialize(Name = "see", Profiles = ["Admin"])] public int C { get; set; }
                    [DontSerialize] public int D { get; set; }
                    [DontSerialize("Public")] public int E { get; set; }
                }
                [ChrysalisService(Name = "Renamed", Namespace = "x.y", Route = "r")]
                public interface IOriginal { [ChrysalisName("Op")] Data Method([ChrysalisName("input")] Data value); }
                """);

            Assert.Empty(generator);
            Assert.Empty(compilation);
            Assert.Contains("\"Renamed\", \"x.y\"", source);
            Assert.Contains("new global::Butterfly.Serialization.SerializableMember(\"A\", 10,", source);
            Assert.Contains("new global::Butterfly.Serialization.SerializableMember(\"B\", 1,", source);
            Assert.Contains("new global::Butterfly.Serialization.SerializableMember(\"see\", 2,", source);
            Assert.Contains("hasExplicitName: true, includedIn: new string[] { \"Admin\" }", source);
            Assert.Contains("excludedFrom: new string[] { \"Public\" }", source);
            Assert.DoesNotContain("\"D\"", source);
            Assert.Contains("new global::Butterfly.Chrysalis.ChrysalisOperation(\"Op\"", source);
            Assert.Contains("ChrysalisParameter(\"input\"", source);
        }

        [Theory]
        [InlineData("HttpGet", "GET")]
        [InlineData("HttpPost", "POST")]
        [InlineData("HttpPut", "PUT")]
        [InlineData("HttpPatch", "PATCH")]
        [InlineData("HttpDelete", "DELETE")]
        [InlineData("HttpQuery", "QUERY")]
        public void HttpAttributesBindTheirMethod(string attribute, string method)
        {
            var (generator, compilation, source) = Run($$"""
                public class Filter { public string? Text { get; set; } }
                [ChrysalisService]
                public interface ICatalog { [{{attribute}}("items/search")] List<string> Find(Filter filter); }
                """);

            Assert.Empty(generator);
            Assert.Empty(compilation);
            Assert.Contains($"new global::Butterfly.Chrysalis.ChrysalisHttpBinding(\"{method}\", \"items/search\")", source);
        }

        [Fact]
        public void DescribesContractsAndSerializerCallsWithoutServices()
        {
            var (generator, compilation, source) = Run("""
                namespace Plain;
                [SerializationContract(Mode = SerializationMode.OptIn)]
                public class Marked { [Serialize] public int Kept { get; set; } public int Dropped { get; set; } }
                public record Unmarked(string Text);
                public static class Usage
                {
                    public static byte[] Save(SerializationFormat format) => ButterflySerializer.Serialize(new List<Unmarked>(), format);
                    public static Unmarked Load(string text, SerializationFormat format) => ButterflySerializer.Deserialize<Unmarked>(text, format);
                }
                """);

            Assert.Empty(generator);
            Assert.Empty(compilation);
            Assert.Contains("Register(typeof(global::Plain.Marked)", source);
            Assert.Contains("Register(typeof(global::System.Collections.Generic.List<global::Plain.Unmarked>)", source);
            Assert.Contains("Register(typeof(global::Plain.Unmarked)", source);
            Assert.Contains("SerializableMember(\"Kept\"", source);
            Assert.DoesNotContain("\"Dropped\"", source);
            Assert.DoesNotContain("ChrysalisRegistry", source);
        }
    }
}

using System.Buffers;
using System.Text;

namespace Butterfly.Serialization.Yaml
{
    /// <summary>
    /// YAML 1.2. Writing uses block style (two-space indentation), quotes every string that could be read as something
    /// else, and tags bytes as !!binary. Reading supports block and flow collections, plain, quoted and block scalars
    /// (| and &gt; with chomping), anchors and aliases, tags (!!str, !!binary, !!int...), comments and document markers;
    /// plain scalars resolve with the core schema (null, booleans, integers, floats). Members are camelCase by default.
    /// </summary>
    public sealed class YamlFormat : ValueFormat
    {
        public static YamlFormat Instance { get; } = new();

        public override string Name => "yaml";
        public override string MediaType => "application/yaml";
        public override bool IsText => true;
        public override ValueConventions Conventions { get; } = new(NamingPolicy.CamelCase, EnumsAsNames: true, ScalarsFromText: true);

        public override void WriteValue(IBufferWriter<byte> output, SerializationValue value, SerializationProfile profile) =>
            output.Write(Encoding.UTF8.GetBytes(YamlWriter.Write(value)));

        public override SerializationValue ReadValue(ReadOnlyMemory<byte> input, SerializationProfile profile)
        {
            string text;
            try
            {
                text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(input.Span).TrimStart('﻿');
            }
            catch (DecoderFallbackException)
            {
                throw new SerializationException("", "the YAML document is not valid UTF-8.");
            }
            return new YamlReader(text, profile.MaxDepth).ReadDocument();
        }
    }
}

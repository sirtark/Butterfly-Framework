using System.Collections.Concurrent;
using System.Text;

namespace Butterfly.Serialization
{
    public enum NamingPolicy : byte
    {
        /// <summary>What the format usually expects: camelCase for JSON, YAML, MessagePack and CBOR; declared names for XML and CSV.</summary>
        Default,
        AsDeclared,
        CamelCase,
        PascalCase,
        SnakeCase,
        KebabCase
    }

    public enum EnumFormat : byte
    {
        /// <summary>Member names in text formats, numbers in binary formats.</summary>
        Default,
        Name,
        Number
    }

    /// <summary>
    /// A named way of serializing: which members are visible (members restricted with <see cref="SerializeAttribute.Profiles"/>
    /// or <see cref="DontSerializeAttribute.Profiles"/>) and how values are written. Reading with a profile ignores the
    /// members it hides, so a "Public" profile also protects against setting hidden properties (mass assignment).
    /// </summary>
    public sealed record SerializationProfile
    {
        private static readonly ConcurrentDictionary<string, SerializationProfile> Registered = new(StringComparer.OrdinalIgnoreCase);

        public SerializationProfile(string name)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            Name = name;
        }

        public string Name { get; init; }
        public NamingPolicy Naming { get; init; } = NamingPolicy.Default;
        public EnumFormat Enums { get; init; } = EnumFormat.Default;
        /// <summary>Leave out members whose value is null. Default: true.</summary>
        public bool IgnoreNullValues { get; init; } = true;
        /// <summary>Leave out members whose value is the default of their type (0, false, ""...). Default: false.</summary>
        public bool IgnoreDefaultValues { get; init; }
        /// <summary>Human-friendly layout for text formats (indentation, line breaks).</summary>
        public bool Indented { get; init; }
        /// <summary>Deepest nesting accepted when writing and reading (protects against stack exhaustion).</summary>
        public int MaxDepth { get; init; } = 64;
        /// <summary>Fail when the input has members the contract does not know (instead of ignoring them).</summary>
        public bool RejectUnknownMembers { get; init; }

        public static SerializationProfile Default { get; } = new("Default");

        /// <summary>Makes a profile available by name (for example to choose it from configuration).</summary>
        public static void Register(SerializationProfile profile)
        {
            ArgumentNullException.ThrowIfNull(profile);
            Registered[profile.Name] = profile;
        }

        /// <summary>A registered profile, or a profile with default settings and that name (members restricted to it are visible).</summary>
        public static SerializationProfile Get(string name) =>
            Registered.TryGetValue(name, out var profile) ? profile : name.Equals(Default.Name, StringComparison.OrdinalIgnoreCase) ? Default : new SerializationProfile(name);

        /// <summary>The name of a member on the wire under this profile.</summary>
        public string MemberName(SerializableMember member, NamingPolicy formatDefault)
        {
            if (member.HasExplicitName)
                return member.Name;
            var policy = Naming == NamingPolicy.Default ? formatDefault : Naming;
            return Apply(policy, member.Name);
        }

        public bool WritesEnumsAsNames(bool formatDefault) => Enums == EnumFormat.Default ? formatDefault : Enums == EnumFormat.Name;

        public static string Apply(NamingPolicy policy, string name) => policy switch
        {
            NamingPolicy.CamelCase => name.Length == 0 || char.IsLower(name[0]) ? name : Words(name, "", lowerFirst: true, upperEach: true),
            NamingPolicy.PascalCase => Words(name, "", lowerFirst: false, upperEach: true),
            NamingPolicy.SnakeCase => Words(name, "_", lowerFirst: true, upperEach: false),
            NamingPolicy.KebabCase => Words(name, "-", lowerFirst: true, upperEach: false),
            _ => name
        };

        // Splits "HTTPServerName" into HTTP, Server, Name (and "snake_case" at its separators), then joins them.
        private static string Words(string name, string separator, bool lowerFirst, bool upperEach)
        {
            var words = new List<string>();
            var current = new StringBuilder();
            for (var i = 0; i < name.Length; i++)
            {
                var c = name[i];
                if (c is '_' or '-' or ' ')
                {
                    Flush();
                    continue;
                }
                var boundary = current.Length > 0 && char.IsUpper(c)
                    && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1])));
                if (boundary)
                    Flush();
                current.Append(c);
            }
            Flush();

            var result = new StringBuilder(name.Length + words.Count);
            for (var i = 0; i < words.Count; i++)
            {
                var word = words[i].ToLowerInvariant();
                if (i > 0)
                    result.Append(separator);
                if (upperEach && !(i == 0 && lowerFirst))
                    result.Append(char.ToUpperInvariant(word[0])).Append(word, 1, word.Length - 1);
                else
                    result.Append(word);
            }
            return result.ToString();

            void Flush()
            {
                if (current.Length > 0)
                    words.Add(current.ToString());
                current.Clear();
            }
        }
    }
}

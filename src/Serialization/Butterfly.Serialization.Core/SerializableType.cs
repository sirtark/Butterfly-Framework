using System.Collections;
using System.Globalization;

namespace Butterfly.Serialization
{
    public enum TypeKind : byte
    {
        Boolean,
        Int32,
        Int64,
        Double,
        Decimal,
        String,
        Bytes,
        /// <summary>A point in time (DateTimeOffset or DateTime).</summary>
        Timestamp,
        Guid,
        Enum,
        /// <summary>A class, record or struct with members.</summary>
        Object,
        List,
        /// <summary>A dictionary with string keys.</summary>
        Map,
        /// <summary>A value whose shape is only known at run time (object, SerializationValue, JsonNode...).</summary>
        Dynamic,
        /// <summary>A length of time (TimeSpan).</summary>
        Duration
    }

    /// <summary>
    /// Format-independent description of a type. Every format encodes values from these descriptions, so a contract is
    /// described once (by the source generator, without reflection) and serialized as JSON, XML, Protocol Buffers...
    /// </summary>
    public abstract class SerializableType
    {
        private protected SerializableType(TypeKind kind, string name, Type clrType)
        {
            Kind = kind;
            Name = name;
            ClrType = clrType;
        }

        public TypeKind Kind { get; }
        /// <summary>Name in schemas (WSDL, .proto) and root elements.</summary>
        public string Name { get; }
        public Type ClrType { get; }

        /// <summary>The value of a missing, non-optional value of this type (0, false, "", empty list...).</summary>
        public abstract object? CreateDefault();

        public bool IsScalar => Kind is not (TypeKind.Object or TypeKind.List or TypeKind.Map or TypeKind.Dynamic);

        public override string ToString() => Name;

        public static SerializableType Boolean { get; } = new ScalarType(TypeKind.Boolean, "bool", typeof(bool), false);
        public static SerializableType Int32 { get; } = new ScalarType(TypeKind.Int32, "int32", typeof(int), 0);
        public static SerializableType Int64 { get; } = new ScalarType(TypeKind.Int64, "int64", typeof(long), 0L);
        public static SerializableType Double { get; } = new ScalarType(TypeKind.Double, "double", typeof(double), 0d);
        public static SerializableType Decimal { get; } = new ScalarType(TypeKind.Decimal, "decimal", typeof(decimal), 0m);
        public static SerializableType String { get; } = new ScalarType(TypeKind.String, "string", typeof(string), string.Empty);
        public static SerializableType Bytes { get; } = new ScalarType(TypeKind.Bytes, "bytes", typeof(byte[]), Array.Empty<byte>());
        public static SerializableType Timestamp { get; } = new ScalarType(TypeKind.Timestamp, "timestamp", typeof(DateTimeOffset), default(DateTimeOffset));
        /// <summary>A timestamp declared as <see cref="System.DateTime"/>: values are read back as UTC DateTime.</summary>
        public static SerializableType DateTime { get; } = new ScalarType(TypeKind.Timestamp, "timestamp", typeof(DateTime), default(DateTime));
        public static SerializableType Guid { get; } = new ScalarType(TypeKind.Guid, "guid", typeof(Guid), System.Guid.Empty);
        public static SerializableType Duration { get; } = new ScalarType(TypeKind.Duration, "duration", typeof(TimeSpan), TimeSpan.Zero);

        /// <summary>The descriptor of a scalar CLR type, or null.</summary>
        public static SerializableType? ForScalar(Type type) => type switch
        {
            _ when type == typeof(bool) => Boolean,
            _ when type == typeof(int) => Int32,
            _ when type == typeof(long) => Int64,
            _ when type == typeof(double) => Double,
            _ when type == typeof(decimal) => Decimal,
            _ when type == typeof(string) => String,
            _ when type == typeof(byte[]) => Bytes,
            _ when type == typeof(DateTimeOffset) => Timestamp,
            _ when type == typeof(DateTime) => DateTime,
            _ when type == typeof(Guid) => Guid,
            _ when type == typeof(TimeSpan) => Duration,
            _ => null
        };
    }

    public sealed class ScalarType : SerializableType
    {
        private readonly object defaultValue;

        internal ScalarType(TypeKind kind, string name, Type clrType, object defaultValue) : base(kind, name, clrType)
        {
            this.defaultValue = defaultValue;
        }

        // Scalars are immutable except byte[], which is never mutated by Butterfly.Serialization.
        public override object? CreateDefault() => defaultValue;
    }

    public readonly record struct EnumMember(string Name, long Number);

    public sealed class EnumType : SerializableType
    {
        private readonly Func<object, long> toNumber;
        private readonly Func<long, object> fromNumber;
        private readonly Dictionary<string, EnumMember> byName;

        public EnumType(string name, Type clrType, IReadOnlyList<EnumMember> members, Func<object, long> toNumber, Func<long, object> fromNumber)
            : base(TypeKind.Enum, name, clrType)
        {
            Members = members;
            this.toNumber = toNumber;
            this.fromNumber = fromNumber;
            byName = new Dictionary<string, EnumMember>(StringComparer.OrdinalIgnoreCase);
            foreach (var member in members)
                byName.TryAdd(member.Name, member);
        }

        public IReadOnlyList<EnumMember> Members { get; }

        public long ToNumber(object value) => toNumber(value);
        public object FromNumber(long number) => fromNumber(number);

        /// <summary>The member name of a value, or its number when it is not a declared member (flags, unknown values).</summary>
        public string GetName(object value)
        {
            var number = toNumber(value);
            foreach (var member in Members)
            {
                if (member.Number == number)
                    return member.Name;
            }
            return number.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Parses a member name (case-insensitive) or a number.</summary>
        public bool TryParse(string text, out object value)
        {
            if (byName.TryGetValue(text, out var member))
            {
                value = fromNumber(member.Number);
                return true;
            }
            if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number))
            {
                value = fromNumber(number);
                return true;
            }
            value = fromNumber(0);
            return false;
        }

        public override object? CreateDefault() => fromNumber(0);
    }

    public sealed class ListType : SerializableType
    {
        private readonly Func<IReadOnlyList<object?>, object> create;

        /// <param name="create">Builds the declared collection (array, List&lt;T&gt;...) from the elements.</param>
        public ListType(SerializableType element, Type clrType, Func<IReadOnlyList<object?>, object> create)
            : base(TypeKind.List, "list<" + element.Name + ">", clrType)
        {
            Element = element;
            this.create = create;
        }

        public SerializableType Element { get; }

        public object Create(IReadOnlyList<object?> items) => create(items);

        public IEnumerable<object?> Enumerate(object list)
        {
            foreach (var item in (IEnumerable)list)
                yield return item;
        }

        public override object? CreateDefault() => create([]);
    }

    /// <summary>A dictionary with string keys (JSON object, protobuf map&lt;string, T&gt;, YAML mapping...).</summary>
    public sealed class MapType : SerializableType
    {
        private readonly Func<IReadOnlyList<KeyValuePair<string, object?>>, object> create;
        private readonly Func<object, IEnumerable<KeyValuePair<string, object?>>> enumerate;

        /// <param name="valueIsOptional">Values may be null (Dictionary&lt;string, int?&gt;, Dictionary&lt;string, string?&gt;).</param>
        public MapType(SerializableType value, Type clrType, Func<IReadOnlyList<KeyValuePair<string, object?>>, object> create,
            Func<object, IEnumerable<KeyValuePair<string, object?>>> enumerate, bool valueIsOptional = false)
            : base(TypeKind.Map, "map<string, " + value.Name + ">", clrType)
        {
            Value = value;
            ValueIsOptional = valueIsOptional;
            this.create = create;
            this.enumerate = enumerate;
        }

        public SerializableType Value { get; }
        public bool ValueIsOptional { get; }

        public object Create(IReadOnlyList<KeyValuePair<string, object?>> entries) => create(entries);
        public IEnumerable<KeyValuePair<string, object?>> Enumerate(object map) => enumerate(map);

        public override object? CreateDefault() => create([]);
    }

    public sealed class SerializableMember
    {
        /// <param name="isOptional">The value may be missing: a nullable reference or value type.</param>
        /// <param name="get">Reads the member from an instance.</param>
        public SerializableMember(string name, int number, SerializableType type, bool isOptional, Func<object, object?> get,
            bool hasExplicitName = false, IReadOnlyList<string>? includedIn = null, IReadOnlyList<string>? excludedFrom = null)
        {
            if (number < 1 || number > 536_870_911 || number is >= 19_000 and <= 19_999)
                throw new ArgumentOutOfRangeException(nameof(number), number, "Member numbers must be between 1 and 536870911, excluding 19000-19999.");

            Name = name;
            Number = number;
            Type = type;
            IsOptional = isOptional;
            Get = get;
            HasExplicitName = hasExplicitName;
            IncludedIn = includedIn ?? [];
            ExcludedFrom = excludedFrom ?? [];
        }

        /// <summary>The declared name (or the explicit one of <see cref="SerializeAttribute.Name"/>).</summary>
        public string Name { get; }
        public int Number { get; }
        public SerializableType Type { get; }
        public bool IsOptional { get; }
        public Func<object, object?> Get { get; }
        /// <summary>The name was set explicitly: naming policies do not change it.</summary>
        public bool HasExplicitName { get; }
        /// <summary>Profiles the member is restricted to; empty means all.</summary>
        public IReadOnlyList<string> IncludedIn { get; }
        /// <summary>Profiles the member is hidden from.</summary>
        public IReadOnlyList<string> ExcludedFrom { get; }
        /// <summary>Position in <see cref="ObjectType.Members"/>, and in the array given to <see cref="ObjectType.Create"/>.</summary>
        public int Index { get; internal set; }

        public bool IsVisibleIn(SerializationProfile profile) =>
            (IncludedIn.Count == 0 || IncludedIn.Contains(profile.Name, StringComparer.OrdinalIgnoreCase))
            && !ExcludedFrom.Contains(profile.Name, StringComparer.OrdinalIgnoreCase);

        /// <summary>The value of the member when it is missing from the input.</summary>
        public object? CreateDefault() => IsOptional ? null : Type.CreateDefault();

        public override string ToString() => $"{Name} = {Number} ({Type})";
    }

    public sealed class ObjectType : SerializableType
    {
        private IReadOnlyList<SerializableMember> members = [];
        private Dictionary<string, SerializableMember> byName = [];
        private Dictionary<int, SerializableMember> byNumber = [];
        private Func<object?[], object>? create;

        /// <summary>Creates the type; its members are set by <see cref="Initialize"/>, so types can refer to each other (and themselves).</summary>
        public ObjectType(string name, string @namespace, Type clrType) : base(TypeKind.Object, name, clrType)
        {
            Namespace = @namespace;
        }

        public string Namespace { get; }
        public IReadOnlyList<SerializableMember> Members => members;

        /// <param name="create">Builds an instance from one value per member, in the order of <paramref name="members"/>.</param>
        public void Initialize(IReadOnlyList<SerializableMember> members, Func<object?[], object> create)
        {
            if (this.create is not null)
                throw new InvalidOperationException($"The type '{Name}' is already initialized.");

            var names = new Dictionary<string, SerializableMember>(StringComparer.OrdinalIgnoreCase);
            var numbers = new Dictionary<int, SerializableMember>();
            for (var i = 0; i < members.Count; i++)
            {
                var member = members[i];
                member.Index = i;
                if (!names.TryAdd(member.Name, member))
                    throw new ArgumentException($"The type '{Name}' has two members named '{member.Name}'.", nameof(members));
                if (!numbers.TryAdd(member.Number, member))
                    throw new ArgumentException($"The type '{Name}' has two members numbered {member.Number}.", nameof(members));
            }

            this.members = members;
            byName = names;
            byNumber = numbers;
            this.create = create;
        }

        /// <summary>Finds a member by its declared name (case-insensitive).</summary>
        public SerializableMember? FindMember(string name) => byName.GetValueOrDefault(name);
        public SerializableMember? FindMember(int number) => byNumber.GetValueOrDefault(number);

        /// <summary>Finds a member by its name on the wire under a profile and naming policy, then by its declared name.</summary>
        public SerializableMember? FindMember(string wireName, SerializationProfile profile, NamingPolicy formatDefault)
        {
            foreach (var member in members)
            {
                if (string.Equals(profile.MemberName(member, formatDefault), wireName, StringComparison.OrdinalIgnoreCase))
                    return member;
            }
            return FindMember(wireName);
        }

        public object Create(object?[] values) =>
            (create ?? throw new InvalidOperationException($"The type '{Name}' is not initialized."))(values);

        /// <summary>One default value per member, ready to be overwritten by a reader and passed to <see cref="Create"/>.</summary>
        public object?[] CreateDefaultValues()
        {
            var values = new object?[members.Count];
            for (var i = 0; i < values.Length; i++)
                values[i] = members[i].CreateDefault();
            return values;
        }

        public override object? CreateDefault() => null;

        /// <summary>
        /// An object backed by an object?[] (one slot per member): how parameter lists and results are described, so they
        /// are encoded exactly like any other object.
        /// </summary>
        public static ObjectType CreateArrayBacked(string name, string @namespace, IEnumerable<(string Name, int Number, SerializableType Type, bool IsOptional)> members)
        {
            var type = new ObjectType(name, @namespace, typeof(object?[]));
            var list = members.Select((member, index) => new SerializableMember(member.Name, member.Number, member.Type, member.IsOptional, instance => ((object?[])instance)[index])).ToArray();
            type.Initialize(list, values => values);
            return type;
        }
    }
}

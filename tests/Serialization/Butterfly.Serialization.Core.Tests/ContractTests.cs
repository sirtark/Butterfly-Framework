using Butterfly.Serialization.Tests;
using Butterfly.Serialization.Json;
using System.Text.Json.Nodes;

namespace Butterfly.Serialization.Core.Tests
{
    // What the generator described, and how profiles and attributes shape it. JSON is used to observe the result.
    public class ContractTests
    {
        private static ObjectType CustomerType => (ObjectType)SerializationRegistry.Get<Customer>();

        [Fact]
        public void DescribesMarkedContracts()
        {
            var type = CustomerType;

            Assert.Equal("Customer", type.Name);
            Assert.Equal(["Id", "Name", "Email", "CreditLimit", "tag_list", "Status", "Address", "Scores", "Labels"], type.Members.Select(member => member.Name));
            Assert.Null(type.FindMember("Password"));
            Assert.Equal(20, type.FindMember("tag_list")!.Number);
            Assert.True(type.FindMember("tag_list")!.HasExplicitName);
            Assert.Equal(["Admin"], type.FindMember("Email")!.IncludedIn);
            Assert.Equal(["Public"], type.FindMember("CreditLimit")!.ExcludedFrom);
            Assert.Equal(TypeKind.Map, type.FindMember("Scores")!.Type.Kind);
            Assert.True(((MapType)type.FindMember("Labels")!.Type).ValueIsOptional);
        }

        [Fact]
        public void OptInContractsOnlyKeepMarkedMembers()
        {
            var type = (ObjectType)SerializationRegistry.Get<Secretive>();

            Assert.Equal(["Visible"], type.Members.Select(member => member.Name));
            Assert.Equal("""{"visible":1}""", ButterflySerializer.SerializeToString(new Secretive { Visible = 1, Hidden = 2 }, JsonFormat.Instance));
        }

        [Fact]
        public void TypesUsedThroughTheSerializerAreDescribedWithoutAttributes()
        {
            // Address has no attribute: Customer uses it, and this call uses List<Address>.
            var json = ButterflySerializer.SerializeToString(new List<Address> { new("A", "B") }, JsonFormat.Instance);

            Assert.Equal("""[{"street":"A","city":"B"}]""", json);
            Assert.True(SerializationRegistry.IsRegistered(typeof(List<Address>)));
            Assert.Equal(new Address("x", "y"), ButterflySerializer.Deserialize<Address>("""{"street":"x","city":"y"}""", JsonFormat.Instance));
        }

        [Fact]
        public void ScalarsDynamicsAndNullablesNeedNoDescription()
        {
            Assert.Equal("42", ButterflySerializer.SerializeToString(42, JsonFormat.Instance));
            Assert.Equal("null", ButterflySerializer.SerializeToString<int?>(null, JsonFormat.Instance));
            Assert.Equal(5, ButterflySerializer.Deserialize<int?>("5", JsonFormat.Instance));
            Assert.Equal("""{"a":[1,"x"]}""", ButterflySerializer.SerializeToString<object>(new Dictionary<string, object?> { ["a"] = new object[] { 1, "x" } }, JsonFormat.Instance));
            Assert.Equal("dark", ButterflySerializer.Deserialize<JsonNode>("""{"theme":"dark"}""", JsonFormat.Instance)!["theme"]!.GetValue<string>());
        }

        [Fact]
        public void UndescribedTypesAreExplained()
        {
            var exception = Assert.Throws<InvalidOperationException>(() => SerializationRegistry.Get(typeof(Uri)));

            Assert.Contains("[SerializationContract]", exception.Message);
        }

        [Fact]
        public void TheDefaultProfileHidesRestrictedMembers()
        {
            var json = ButterflySerializer.SerializeToString(Samples.Customer(), JsonFormat.Instance);

            Assert.DoesNotContain("email", json);
            Assert.Contains("\"creditLimit\":1500", json);
            Assert.DoesNotContain("hunter2", json);
            Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"tag_list\":[\"vip\",\"early\"]", json);   // explicit names ignore the naming policy
            Assert.DoesNotContain("missing", json);                       // null map values are omitted
        }

        [Fact]
        public void ProfilesChooseMembers()
        {
            var admin = ButterflySerializer.SerializeToString(Samples.Customer(), JsonFormat.Instance, new SerializationProfile("Admin"));
            var publicView = ButterflySerializer.SerializeToString(Samples.Customer(), JsonFormat.Instance, new SerializationProfile("Public"));

            Assert.Contains("\"email\":\"ada@example.com\"", admin);
            Assert.Contains("creditLimit", admin);
            Assert.DoesNotContain("email", publicView);
            Assert.DoesNotContain("creditLimit", publicView);
        }

        [Fact]
        public void HiddenMembersCannotBeSetFromTheInput()
        {
            // Mass assignment protection: the Public profile ignores creditLimit and email in the input.
            const string json = """{"id":1,"name":"Eve","creditLimit":1000000,"email":"eve@evil.com","password":"x"}""";

            var publicRead = ButterflySerializer.Deserialize<Customer>(json, JsonFormat.Instance, new SerializationProfile("Public"));
            var adminRead = ButterflySerializer.Deserialize<Customer>(json, JsonFormat.Instance, new SerializationProfile("Admin"));

            Assert.Equal(0m, publicRead.CreditLimit);
            Assert.Null(publicRead.Email);
            Assert.Equal("never serialized", publicRead.Password);
            Assert.Equal(1000000m, adminRead.CreditLimit);
            Assert.Equal("eve@evil.com", adminRead.Email);
        }

        [Theory]
        [InlineData(NamingPolicy.AsDeclared, "CreditLimit")]
        [InlineData(NamingPolicy.CamelCase, "creditLimit")]
        [InlineData(NamingPolicy.PascalCase, "CreditLimit")]
        [InlineData(NamingPolicy.SnakeCase, "credit_limit")]
        [InlineData(NamingPolicy.KebabCase, "credit-limit")]
        public void ProfilesChooseTheNaming(NamingPolicy naming, string expected)
        {
            var json = ButterflySerializer.SerializeToString(Samples.Customer(), JsonFormat.Instance, new SerializationProfile("Naming") { Naming = naming });

            Assert.Contains($"\"{expected}\":", json);
            // Reading accepts the same names back.
            Assert.Equal(1500m, ButterflySerializer.Deserialize<Customer>(json, JsonFormat.Instance, new SerializationProfile("Naming") { Naming = naming }).CreditLimit);
        }

        [Theory]
        [InlineData("HTTPServerName", "http_server_name", "httpServerName")]
        [InlineData("userID2", "user_id2", "userID2")]   // already camel: only the first letter is lowered, as System.Text.Json does
        [InlineData("already_snake", "already_snake", "alreadySnake")]
        public void NamingPoliciesSplitWords(string name, string snake, string camel)
        {
            Assert.Equal(snake, SerializationProfile.Apply(NamingPolicy.SnakeCase, name));
            Assert.Equal(camel, SerializationProfile.Apply(NamingPolicy.CamelCase, name == "already_snake" ? "AlreadySnake" : name));
        }

        [Fact]
        public void ProfilesChooseEnumsNullsAndDefaults()
        {
            var customer = new Customer { Id = 0, Name = "", Status = Status.Archived };
            var profile = new SerializationProfile("Compact") { Enums = EnumFormat.Number, IgnoreDefaultValues = true };

            var compact = ButterflySerializer.SerializeToString(customer, JsonFormat.Instance, profile);
            var withNulls = ButterflySerializer.SerializeToString(customer, JsonFormat.Instance, new SerializationProfile("Nulls") { IgnoreNullValues = false });

            Assert.Equal("""{"status":10}""", compact);
            Assert.Contains("\"status\":\"archived\"", withNulls);   // [EnumName]
            Assert.Contains("\"address\":null", withNulls);
        }

        [Fact]
        public void IndentedProfilesAreReadable()
        {
            var json = ButterflySerializer.SerializeToString(new Address("A", "B"), JsonFormat.Instance, new SerializationProfile("Pretty") { Indented = true });

            Assert.Contains("\n  \"street\": \"A\"", json.ReplaceLineEndings("\n"));
        }

        [Fact]
        public void UnknownMembersCanBeRejected()
        {
            var strict = new SerializationProfile("Strict") { RejectUnknownMembers = true };

            Assert.Equal("x", ButterflySerializer.Deserialize<Address>("""{"street":"x","city":"y","planet":"Mars"}""", JsonFormat.Instance).Street);
            var exception = Assert.Throws<SerializationException>(() => ButterflySerializer.Deserialize<Address>("""{"street":"x","planet":"Mars"}""", JsonFormat.Instance, strict));
            Assert.Equal("planet", exception.Path);
        }

        [Fact]
        public void RegisteredProfilesAreFoundByName()
        {
            SerializationProfile.Register(new SerializationProfile("Reporting") { Naming = NamingPolicy.SnakeCase });

            Assert.Equal(NamingPolicy.SnakeCase, SerializationProfile.Get("reporting").Naming);
            Assert.Equal("Unregistered", SerializationProfile.Get("Unregistered").Name);
            Assert.Same(SerializationProfile.Default, SerializationProfile.Get("default"));
        }

        [Fact]
        public void DepthIsLimited()
        {
            var deep = new AllKinds();
            for (var i = 0; i < 40; i++)
                deep = new AllKinds { Children = [deep] };

            var exception = Assert.Throws<SerializationException>(() => ButterflySerializer.Serialize(deep, JsonFormat.Instance, new SerializationProfile("Shallow") { MaxDepth = 20 }));
            Assert.Contains("nested deeper than 20", exception.Message);
        }
    }

    public class SerializationValueTests
    {
        [Fact]
        public void ComparesStructurally()
        {
            var a = SerializationValue.Object([new("n", SerializationValue.From(1L)), new("list", SerializationValue.Array([SerializationValue.From("x")]))]);
            var b = SerializationValue.Object([new("n", SerializationValue.From(1.0m)), new("list", SerializationValue.Array([SerializationValue.From("x")]))]);

            Assert.Equal(a, b);
            Assert.Equal("""{"n":1,"list":["x"]}""", a.ToString());
            Assert.Equal(1L, a["n"]!.AsInt64());
            Assert.Null(a["missing"]);
        }

        [Fact]
        public void ConvertsClrObjectsAndBack()
        {
            var value = DynamicType.Object.ToValue(new Dictionary<string, object?> { ["when"] = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), ["id"] = Guid.Empty, ["status"] = Status.Active, ["address"] = new Address("a", "b") });

            Assert.Equal(ValueKind.Timestamp, value["when"]!.Kind);
            Assert.Equal("00000000-0000-0000-0000-000000000000", value["id"]!.AsString());
            Assert.Equal("Active", value["status"]!.AsString());
            Assert.Equal("a", value["address"]!["Street"]!.AsString());   // described contracts keep declared names

            var back = (Dictionary<string, object?>)DynamicType.Object.FromValue(value)!;
            Assert.IsType<DateTimeOffset>(back["when"]);
        }

        [Fact]
        public void ConvertsJsonNodesAndElements()
        {
            var node = JsonNode.Parse("""{"a":[1,2.5,"x",true,null],"b":{"c":12345678901234567890}}""");

            var value = DynamicType.JsonNode.ToValue(node);
            var element = (System.Text.Json.JsonElement)DynamicType.JsonElement.FromValue(value)!;

            Assert.Equal(ValueKind.Integer, value["a"]![0].Kind);
            Assert.Equal(ValueKind.Decimal, value["a"]![1].Kind);
            Assert.Equal(ValueKind.Decimal, value["b"]!["c"]!.Kind);   // beyond long: exact decimal
            Assert.Equal(node!.ToJsonString(), System.Text.Json.JsonSerializer.Serialize(element, System.Text.Json.JsonSerializerOptions.Default));
            Assert.Equal(node.ToJsonString(), ((JsonNode)DynamicType.JsonNode.FromValue(value)!).ToJsonString());
        }

        [Fact]
        public void TypesThatAreNotContractsCannotBeDynamic()
        {
            var exception = Assert.Throws<SerializationException>(() => DynamicType.Object.ToValue(new Uri("https://x")));

            Assert.Contains("not a described contract", exception.Message);
        }
    }
}

using System.Text.Json.Nodes;

namespace Butterfly.Serialization.Tests
{
    public enum Status
    {
        Draft,
        Active,
        [EnumName("archived")]
        Archived = 10
    }

    public sealed record Address(string Street, string City, string? Zip = null);

    // Profiles: "Admin" sees the email, "Public" does not see the credit limit; nobody sees the password.
    [SerializationContract]
    public sealed class Customer
    {
        public int Id { get; init; }
        public string Name { get; init; } = "";
        [Serialize(Profiles = ["Admin"])]
        public string? Email { get; init; }
        [DontSerialize("Public")]
        public decimal CreditLimit { get; init; }
        [DontSerialize]
        public string Password { get; set; } = "never serialized";
        [Serialize(Name = "tag_list", Number = 20)]
        public List<string> Tags { get; init; } = [];
        public Status Status { get; init; }
        public Address? Address { get; init; }
        public Dictionary<string, int> Scores { get; init; } = [];
        public Dictionary<string, string?> Labels { get; init; } = [];
    }

    [SerializationContract(Mode = SerializationMode.OptIn)]
    public sealed class Secretive
    {
        [Serialize]
        public int Visible { get; set; }
        public int Hidden { get; set; }
    }

    // Every kind of value, for round trips.
    [SerializationContract]
    public sealed class AllKinds
    {
        public bool Flag { get; init; }
        public int Number { get; init; }
        public long Big { get; init; }
        public double Ratio { get; init; }
        public decimal Amount { get; init; }
        public string Text { get; init; } = "";
        public string? Note { get; init; }
        public byte[] Data { get; init; } = [];
        public DateTimeOffset When { get; init; }
        public DateTime Day { get; init; }
        public Guid Id { get; init; }
        public Status Status { get; init; }
        public int? Maybe { get; init; }
        public int[] Numbers { get; init; } = [];
        public List<Status> Statuses { get; init; } = [];
        public Address? Address { get; init; }
        public List<AllKinds> Children { get; init; } = [];
        public Dictionary<string, Address> Places { get; init; } = [];
        public object? Anything { get; init; }
        public JsonNode? Json { get; init; }
        public TimeSpan Elapsed { get; init; }
        public TimeSpan? Timeout { get; init; }
    }

    public static class Samples
    {
        public static AllKinds Full() => new()
        {
            Flag = true,
            Number = -42,
            Big = long.MaxValue,
            Ratio = 0.1,
            Amount = 12345.6789m,
            Text = "héllo \"wörld\" <&> : # - [x] {y}\nline 2",
            Note = null,
            Data = [0, 1, 2, 255],
            When = new DateTimeOffset(2026, 10, 4, 15, 30, 45, 123, TimeSpan.FromHours(-3)).AddTicks(4567),
            Day = new DateTime(2026, 10, 4, 0, 0, 0, DateTimeKind.Utc),
            Id = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e"),
            Status = Status.Archived,
            Maybe = 0,
            Numbers = [1, -1, int.MaxValue, int.MinValue],
            Statuses = [Status.Active, Status.Draft],
            Address = new Address("Av. Corrientes 1234", "Buenos Aires", "C1043"),
            Children = [new AllKinds { Number = 1, Text = "child" }],
            Places = new() { ["home"] = new Address("Calle 1", "Rosario"), ["work"] = new Address("Calle 2", "Córdoba", "X5000") },
            Anything = new Dictionary<string, object?> { ["count"] = 3L, ["tags"] = new List<object?> { "a", true, null }, ["price"] = 9.5m },
            Json = JsonNode.Parse("""{"theme":"dark","sizes":[1,2.5],"nested":{"on":true,"none":null}}"""),
            Elapsed = new TimeSpan(1, 2, 3, 4, 567),
            Timeout = TimeSpan.FromMilliseconds(-1500)
        };

        public static void AssertEqual(AllKinds expected, AllKinds actual, bool exactTimestampOffset = false)
        {
            Assert.Equal(expected.Flag, actual.Flag);
            Assert.Equal(expected.Number, actual.Number);
            Assert.Equal(expected.Big, actual.Big);
            Assert.Equal(expected.Ratio, actual.Ratio);
            Assert.Equal(expected.Amount, actual.Amount);
            Assert.Equal(expected.Text, actual.Text);
            Assert.Equal(expected.Note, actual.Note);
            Assert.Equal(expected.Data, actual.Data);
            Assert.Equal(expected.When, actual.When);   // same instant
            if (exactTimestampOffset)
                Assert.Equal(expected.When.Offset, actual.When.Offset);
            Assert.Equal(expected.Day, actual.Day);
            Assert.Equal(expected.Id, actual.Id);
            Assert.Equal(expected.Status, actual.Status);
            Assert.Equal(expected.Maybe, actual.Maybe);
            Assert.Equal(expected.Numbers, actual.Numbers);
            Assert.Equal(expected.Statuses, actual.Statuses);
            Assert.Equal(expected.Address, actual.Address);
            Assert.Equal(expected.Children.Count, actual.Children.Count);
            for (var i = 0; i < expected.Children.Count; i++)
                AssertEqual(expected.Children[i], actual.Children[i], exactTimestampOffset);
            Assert.Equal(expected.Places.OrderBy(p => p.Key), actual.Places.OrderBy(p => p.Key));
            Assert.Equal(Dynamic(expected.Anything), Dynamic(actual.Anything));
            Assert.Equal(expected.Json?.ToJsonString(), actual.Json?.ToJsonString());
            Assert.Equal(expected.Elapsed, actual.Elapsed);
            Assert.Equal(expected.Timeout, actual.Timeout);
        }

        // Dynamic values are compared through their value trees (numbers compare by value, not CLR type).
        public static SerializationValue Dynamic(object? value) => DynamicType.Object.ToValue(value);

        public static Customer Customer() => new()
        {
            Id = 7,
            Name = "Ada",
            Email = "ada@example.com",
            CreditLimit = 1500m,
            Password = "hunter2",
            Tags = ["vip", "early"],
            Status = Status.Active,
            Address = new Address("Main 1", "London"),
            Scores = new() { ["math"] = 10, ["poetry"] = 9 },
            Labels = new() { ["nickname"] = "Countess", ["missing"] = null }
        };
    }
}

using Butterfly.Serialization;
using Butterfly.Serialization.Cbor;
using Butterfly.Serialization.Csv;
using Butterfly.Serialization.Json;
using Butterfly.Serialization.MessagePack;
using Butterfly.Serialization.Protobuf;
using Butterfly.Serialization.Xml;
using Butterfly.Serialization.Yaml;

var customer = new Customer
{
    Id = 7,
    Name = "Ada",
    Email = "ada@example.com",
    CreditLimit = 1500m,
    Password = "hunter2",
    Tags = ["vip", "early"],
    Status = CustomerStatus.Active,
    Since = new DateTimeOffset(2026, 10, 5, 9, 30, 0, TimeSpan.Zero)
};

// Text formats.
SerializationFormat[] text = [JsonFormat.Instance, XmlFormat.Instance, YamlFormat.Instance];
foreach (var format in text)
{
    Console.WriteLine($"--- {format.Name}");
    Console.WriteLine(ButterflySerializer.SerializeToString(customer, format));
}

// CSV is tabular: a list of flat rows.
List<Purchase> purchases = [new("Notebook", 2, 3.5m), new("Pen, blue", 10, 0.8m)];
Console.WriteLine("--- csv");
Console.WriteLine(ButterflySerializer.SerializeToString(purchases, CsvFormat.Instance));

// Binary formats.
SerializationFormat[] binary = [ProtobufFormat.Instance, MessagePackFormat.Instance, CborFormat.Instance];
foreach (var format in binary)
{
    var bytes = ButterflySerializer.Serialize(customer, format);
    var back = ButterflySerializer.Deserialize<Customer>(bytes, format);
    Console.WriteLine($"--- {format.Name}: {bytes.Length} bytes, {Convert.ToHexString(bytes)}");
    Console.WriteLine($"    read back: {back.Name}, {back.Status}, {string.Join('/', back.Tags)}");
}

// Profiles: who sees what, and how it is written.
var admin = new SerializationProfile("Admin") { Naming = NamingPolicy.SnakeCase, Indented = true };
var visitor = new SerializationProfile("Public") { Enums = EnumFormat.Number };
Console.WriteLine("--- JSON, Admin profile");
Console.WriteLine(ButterflySerializer.SerializeToString(customer, JsonFormat.Instance, admin));
Console.WriteLine("--- JSON, Public profile");
Console.WriteLine(ButterflySerializer.SerializeToString(customer, JsonFormat.Instance, visitor));

// Hidden members are never read either: a client cannot set what it cannot see.
var posted = ButterflySerializer.Deserialize<Customer>("""{"name":"Eve","creditLimit":1000000,"password":"x"}""", JsonFormat.Instance, visitor);
Console.WriteLine($"--- Posted as Public: {posted.Name}, credit {posted.CreditLimit}, password '{posted.Password}'");

// Opt-in contracts only carry what is marked.
Console.WriteLine("--- Opt-in contract");
Console.WriteLine(ButterflySerializer.SerializeToString(new Session { User = "ada", Token = "secret" }, JsonFormat.Instance));

// Every member is written unless it says otherwise (opt-out, the default).
[SerializationContract]
public sealed class Customer
{
    public int Id { get; init; }
    public string Name { get; init; } = "";

    // Only in the Admin profile.
    [Serialize(Profiles = ["Admin"])]
    public string? Email { get; init; }

    // Everywhere but the Public profile.
    [DontSerialize("Public")]
    public decimal CreditLimit { get; init; }

    // Never.
    [DontSerialize]
    public string Password { get; set; } = "";

    [Serialize(Name = "tag_list")]
    public List<string> Tags { get; init; } = [];

    public CustomerStatus Status { get; init; }
    public DateTimeOffset Since { get; init; }
}

public enum CustomerStatus
{
    Active,
    [EnumName("on-hold")] OnHold
}

public sealed record Purchase(string Product, int Quantity, decimal Price);

// Only members marked [Serialize] are written.
[SerializationContract(Mode = SerializationMode.OptIn)]
public sealed class Session
{
    [Serialize]
    public string User { get; init; } = "";

    public string Token { get; init; } = "";
}

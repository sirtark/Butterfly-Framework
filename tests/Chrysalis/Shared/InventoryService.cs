using Butterfly.Serialization;
using System.Collections.Concurrent;

namespace Butterfly.Chrysalis.Tests.Inventory
{
    public enum Category
    {
        Books,
        Music,
        Games = 10
    }

    // Positional record: built through its constructor.
    public sealed record Product(int Id, string Name, decimal Price, Category Category, IReadOnlyList<string> Tags, DateTimeOffset? CreatedAt = null);

    // Every supported kind of member, built through init-only setters.
    public sealed class Sample
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
        public Category Kind { get; init; }
        public int? Maybe { get; init; }
        public int[] Numbers { get; init; } = [];
        public List<Category> Kinds { get; init; } = [];
        public Product? Product { get; init; }
        public List<Sample> Children { get; init; } = [];

        [DontSerialize]
        public string Ignored { get; set; } = "ignored";
    }

    [ChrysalisService(Namespace = "inventory.v1", Route = "inventory")]
    public interface IInventoryService
    {
        [HttpGet("products/{id}")]
        Task<Product> GetProduct(int id);

        [HttpGet("products")]
        Task<IReadOnlyList<Product>> Search(string? text, Category? category, CancellationToken cancellationToken);

        [HttpPost("products")]
        Task<Product> AddProduct(Product product);

        // No REST attribute: POST inventory/AddStock with a JSON body of parameters.
        int AddStock(int productId, int quantity);

        [HttpDelete("products/{id}")]
        Task Delete(int id);

        Sample Echo(Sample sample);

        void Fail(ChrysalisStatus status, string message);

        Task<string> Slow(int milliseconds, CancellationToken cancellationToken);

        [ChrysalisName("WhoAmI")]
        string Identify(ChrysalisCallContext context);
    }

    public sealed class InventoryService : IInventoryService
    {
        private readonly ConcurrentDictionary<int, Product> products = new()
        {
            [1] = new Product(1, "Dune", 9.99m, Category.Books, ["sci-fi", "classic"], new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero)),
            [2] = new Product(2, "Kind of Blue", 14.50m, Category.Music, ["jazz"]),
            [3] = new Product(3, "Outer Wilds", 24m, Category.Games, [])
        };
        private readonly ConcurrentDictionary<int, int> stock = new();

        public Task<Product> GetProduct(int id) =>
            products.TryGetValue(id, out var product) ? Task.FromResult(product) : throw new ChrysalisException(ChrysalisStatus.NotFound, $"Product {id} does not exist.");

        public Task<IReadOnlyList<Product>> Search(string? text, Category? category, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Product>>([.. products.Values
                .Where(product => text is null || product.Name.Contains(text, StringComparison.OrdinalIgnoreCase))
                .Where(product => category is null || product.Category == category)
                .OrderBy(product => product.Id)]);

        public Task<Product> AddProduct(Product product)
        {
            if (string.IsNullOrWhiteSpace(product.Name))
                throw new ArgumentException("A product needs a name.");
            if (!products.TryAdd(product.Id, product))
                throw new ChrysalisException(ChrysalisStatus.AlreadyExists, $"Product {product.Id} already exists.");
            return Task.FromResult(product);
        }

        public int AddStock(int productId, int quantity) => stock.AddOrUpdate(productId, quantity, (_, current) => current + quantity);

        public Task Delete(int id) =>
            products.TryRemove(id, out _) ? Task.CompletedTask : throw new KeyNotFoundException($"Product {id} does not exist.");

        public Sample Echo(Sample sample) => sample;

        public void Fail(ChrysalisStatus status, string message) =>
            throw (status == ChrysalisStatus.Internal ? new InvalidCastException(message) : new ChrysalisException(status, message));

        public async Task<string> Slow(int milliseconds, CancellationToken cancellationToken)
        {
            await Task.Delay(milliseconds, cancellationToken);
            return "done";
        }

        public string Identify(ChrysalisCallContext context) => $"{context.Protocol}:{context.User?.Identity?.Name ?? "anonymous"}";
    }

    public static class Samples
    {
        public static Sample Full() => new()
        {
            Flag = true,
            Number = -42,
            Big = long.MaxValue,
            Ratio = 0.1,
            Amount = 12345.6789m,
            Text = "héllo \"wörld\" <&>",
            Note = null,
            Data = [0, 1, 2, 255],
            When = new DateTimeOffset(2026, 10, 4, 15, 30, 45, 123, TimeSpan.Zero).AddTicks(4567),
            Day = new DateTime(2026, 10, 4, 0, 0, 0, DateTimeKind.Utc),
            Id = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e"),
            Kind = Category.Games,
            Maybe = 0,
            Numbers = [1, -1, int.MaxValue, int.MinValue],
            Kinds = [Category.Music, Category.Books],
            Product = new Product(7, "Nested", 1.5m, Category.Music, ["a", "b"], new DateTimeOffset(2025, 5, 6, 7, 8, 9, TimeSpan.Zero)),
            Children = [new Sample { Number = 1, Text = "child" }]
        };

        // Records compare by value, but arrays and lists inside do not: compare the observable content.
        public static void AssertEqual(Sample expected, Sample actual)
        {
            Assert.Equal(expected.Flag, actual.Flag);
            Assert.Equal(expected.Number, actual.Number);
            Assert.Equal(expected.Big, actual.Big);
            Assert.Equal(expected.Ratio, actual.Ratio);
            Assert.Equal(expected.Amount, actual.Amount);
            Assert.Equal(expected.Text, actual.Text);
            Assert.Equal(expected.Note, actual.Note);
            Assert.Equal(expected.Data, actual.Data);
            Assert.Equal(expected.When, actual.When);
            Assert.Equal(expected.Day, actual.Day);
            Assert.Equal(expected.Id, actual.Id);
            Assert.Equal(expected.Kind, actual.Kind);
            Assert.Equal(expected.Maybe, actual.Maybe);
            Assert.Equal(expected.Numbers, actual.Numbers);
            Assert.Equal(expected.Kinds, actual.Kinds);
            AssertEqual(expected.Product, actual.Product);
            Assert.Equal(expected.Children.Count, actual.Children.Count);
            for (var i = 0; i < expected.Children.Count; i++)
                AssertEqual(expected.Children[i], actual.Children[i]);
            Assert.Equal("ignored", actual.Ignored);
        }

        public static void AssertEqual(Product? expected, Product? actual)
        {
            if (expected is null)
            {
                Assert.Null(actual);
                return;
            }
            Assert.NotNull(actual);
            Assert.Equal(expected with { Tags = [] }, actual with { Tags = [] });
            Assert.Equal(expected.Tags, actual.Tags);
        }
    }
}

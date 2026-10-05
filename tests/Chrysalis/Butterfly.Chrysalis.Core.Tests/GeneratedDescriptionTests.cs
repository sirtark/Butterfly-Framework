using Butterfly.Serialization;
using Butterfly.Chrysalis.Tests.Inventory;

namespace Butterfly.Chrysalis.Core.Tests
{
    // What the source generator described for the shared sample service.
    public class GeneratedDescriptionTests
    {
        private static readonly ChrysalisService Service = ChrysalisRegistry.GetService<IInventoryService>();

        [Fact]
        public void DescribesTheService()
        {
            Assert.Equal("InventoryService", Service.Name);
            Assert.Equal("inventory.v1", Service.Namespace);
            Assert.Equal("inventory", Service.Route);
            Assert.Equal(typeof(IInventoryService), Service.ContractType);
            Assert.Equal(["GetProduct", "Search", "AddProduct", "AddStock", "Delete", "Echo", "Fail", "Slow", "WhoAmI"], Service.Operations.Select(operation => operation.Name));
        }

        [Fact]
        public void DescribesParametersWithoutTransportOnes()
        {
            var search = Service.FindOperation("Search")!;

            // The CancellationToken is not part of the contract.
            Assert.Equal(["text", "category"], search.Parameters.Select(parameter => parameter.Name));
            Assert.True(search.Parameters[0].IsOptional);
            Assert.Equal(TypeKind.String, search.Parameters[0].Type.Kind);
            Assert.True(search.Parameters[1].IsOptional);
            Assert.Equal(TypeKind.Enum, search.Parameters[1].Type.Kind);
            Assert.Equal(TypeKind.List, search.ReturnType!.Kind);
            Assert.Equal(new ChrysalisHttpBinding("GET", "products"), search.Http);

            Assert.Empty(Service.FindOperation("WhoAmI")!.Parameters);
            Assert.Null(Service.FindOperation("Delete")!.ReturnType);
            Assert.Null(Service.FindOperation("AddStock")!.Http);
            Assert.Equal("InventoryService.AddStock", Service.FindOperation("AddStock")!.FullName);
        }

        [Fact]
        public void DescribesContracts()
        {
            var product = (ObjectType)Service.FindOperation("GetProduct")!.ReturnType!;

            Assert.Equal("Product", product.Name);
            Assert.Equal(typeof(Product), product.ClrType);
            Assert.Equal(["Id", "Name", "Price", "Category", "Tags", "CreatedAt"], product.Members.Select(field => field.Name));
            Assert.Equal([1, 2, 3, 4, 5, 6], product.Members.Select(field => field.Number));
            Assert.True(product.FindMember("createdAt")!.IsOptional);
            Assert.False(product.FindMember("Name")!.IsOptional);

            var category = (EnumType)product.FindMember("Category")!.Type;
            Assert.Equal([new EnumMember("Books", 0), new EnumMember("Music", 1), new EnumMember("Games", 10)], category.Members);
        }

        [Fact]
        public void IgnoredMembersAreLeftOut()
        {
            var sample = (ObjectType)Service.FindOperation("Echo")!.ReturnType!;

            Assert.Null(sample.FindMember("Ignored"));
            Assert.Same(sample, ((ListType)sample.FindMember("Children")!.Type).Element);
        }

        [Fact]
        public void CreatesAndReadsInstances()
        {
            var product = (ObjectType)Service.FindOperation("GetProduct")!.ReturnType!;
            var values = product.CreateDefaultValues();
            values[0] = 5;
            values[1] = "Created";

            var created = (Product)product.Create(values);

            Assert.Equal(new Product(5, "Created", 0m, Category.Books, [], null) with { Tags = created.Tags }, created);
            Assert.Empty(created.Tags);
            Assert.Equal("Created", product.FindMember("Name")!.Get(created));
        }

        [Fact]
        public void ParametersAndResultsAreMessages()
        {
            var addStock = Service.FindOperation("AddStock")!;

            Assert.Equal("AddStockRequest", addStock.ParametersType.Name);
            Assert.Equal(["productId", "quantity"], addStock.ParametersType.Members.Select(field => field.Name));
            Assert.Equal("result", Assert.Single(addStock.ResultType.Members).Name);
            Assert.Empty(Service.FindOperation("Delete")!.ResultType.Members);
        }

        [Fact]
        public void UnknownContractsAreExplained()
        {
            var exception = Assert.Throws<InvalidOperationException>(() => ChrysalisRegistry.GetService<IDisposable>());
            Assert.Contains("[ChrysalisService]", exception.Message);
        }
    }
}

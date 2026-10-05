using Butterfly.Serialization;
using Butterfly.Chrysalis.Tests.Inventory;
using System.Security.Claims;

namespace Butterfly.Chrysalis.Core.Tests
{
    public class ChrysalisServerTests
    {
        // Calls the server in-process: what a protocol host does, minus the wire.
        private sealed class InProcessInvoker(ChrysalisServer server) : IChrysalisInvoker
        {
            public ValueTask<object?> InvokeAsync(ChrysalisOperation operation, object?[] arguments, CancellationToken cancellationToken) =>
                server.InvokeAsync(new ChrysalisCallContext(operation, arguments, "InProcess", cancellationToken));
        }

        private static (ChrysalisServer Server, IInventoryService Client) Create(ChrysalisServerOptions? options = null)
        {
            var server = new ChrysalisServer(options).Expose<IInventoryService>(new InventoryService());
            return (server, ChrysalisRegistry.CreateClient<IInventoryService>(new InProcessInvoker(server)));
        }

        [Fact]
        public async Task TheGeneratedClientCallsTheService()
        {
            var (_, client) = Create();

            Assert.Equal("Dune", (await client.GetProduct(1)).Name);
            Assert.Equal(2, (await client.Search(null, Category.Books, CancellationToken.None)).Count + 1);
            Assert.Equal(5, client.AddStock(1, 5));
            Assert.Equal(8, client.AddStock(1, 3));
            await client.Delete(3);
            Assert.Equal(2, (await client.Search(null, null, CancellationToken.None)).Count);
            Assert.Equal("InProcess:anonymous", client.Identify(null!));

            var sample = Samples.Full();
            Assert.Same(sample, client.Echo(sample));
        }

        [Fact]
        public void TheClientCanBeCreatedDirectly()
        {
            var server = new ChrysalisServer().Expose<IInventoryService>(new InventoryService());
            IInventoryService client = new InventoryServiceClient(new InProcessInvoker(server));

            Assert.Equal(1, client.AddStock(9, 1));
        }

        [Fact]
        public async Task ServiceErrorsKeepTheirStatus()
        {
            var (_, client) = Create();

            var notFound = await Assert.ThrowsAsync<ChrysalisException>(() => client.GetProduct(99));
            Assert.Equal(ChrysalisStatus.NotFound, notFound.Status);
            Assert.Equal("Product 99 does not exist.", notFound.Message);

            Assert.Equal(ChrysalisStatus.PermissionDenied, Assert.Throws<ChrysalisException>(() => client.Fail(ChrysalisStatus.PermissionDenied, "no")).Status);
        }

        [Fact]
        public async Task CommonExceptionsAreTranslated()
        {
            var (_, client) = Create();

            Assert.Equal(ChrysalisStatus.InvalidArgument, (await Assert.ThrowsAsync<ChrysalisException>(() => client.AddProduct(new Product(9, " ", 1, Category.Books, [])))).Status);
            Assert.Equal(ChrysalisStatus.NotFound, (await Assert.ThrowsAsync<ChrysalisException>(() => client.Delete(42))).Status);
        }

        [Fact]
        public void UnexpectedExceptionsHideTheirDetails()
        {
            var (_, client) = Create();

            var exception = Assert.Throws<ChrysalisException>(() => client.Fail(ChrysalisStatus.Internal, "secret connection string"));

            Assert.Equal(ChrysalisStatus.Internal, exception.Status);
            Assert.Equal("An internal error occurred.", exception.Message);
            Assert.IsType<InvalidCastException>(exception.InnerException);
        }

        [Fact]
        public void DetailsCanBeIncludedOnPurpose()
        {
            var (_, client) = Create(new ChrysalisServerOptions { IncludeExceptionDetails = true });

            Assert.Equal("secret connection string", Assert.Throws<ChrysalisException>(() => client.Fail(ChrysalisStatus.Internal, "secret connection string")).Message);
        }

        [Fact]
        public async Task CancellationReachesTheService()
        {
            var (_, client) = Create();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

            var exception = await Assert.ThrowsAsync<ChrysalisException>(() => client.Slow(10_000, cancellation.Token));

            Assert.Equal(ChrysalisStatus.Cancelled, exception.Status);
        }

        [Fact]
        public void MiddlewareRunsInOrderAroundEveryCall()
        {
            var (server, client) = Create();
            var log = new List<string>();
            server.Use(async (context, next) =>
            {
                log.Add("outer before " + context.Operation.Name);
                var result = await next(context);
                log.Add("outer after");
                return result;
            });
            server.Use((context, next) =>
            {
                log.Add("inner");
                context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "ada")], "test"));
                return next(context);
            });

            Assert.Equal("InProcess:ada", client.Identify(null!));
            Assert.Equal(["outer before WhoAmI", "inner", "outer after"], log);
        }

        [Fact]
        public async Task MiddlewareSeesTranslatedExceptions()
        {
            var (server, client) = Create();
            var seen = new List<ChrysalisStatus>();
            server.Use(async (context, next) =>
            {
                try
                {
                    return await next(context);
                }
                catch (ChrysalisException exception)
                {
                    seen.Add(exception.Status);
                    throw;
                }
            });

            await Assert.ThrowsAsync<ChrysalisException>(() => client.Delete(42));          // KeyNotFoundException
            Assert.Throws<ChrysalisException>(() => client.Fail(ChrysalisStatus.Internal, "x")); // InvalidCastException

            Assert.Equal([ChrysalisStatus.NotFound, ChrysalisStatus.Internal], seen);
        }

        [Fact]
        public void MiddlewareCanRejectAndReplaceArguments()
        {
            var (server, client) = Create();
            server.Use((context, next) =>
            {
                if (context.Operation.Name == "AddStock")
                {
                    if ((int)context.Arguments[1]! < 0)
                        throw new ChrysalisException(ChrysalisStatus.InvalidArgument, "Negative quantity.");
                    context.Arguments[1] = (int)context.Arguments[1]! * 10;
                }
                return next(context);
            });

            Assert.Equal(20, client.AddStock(4, 2));
            Assert.Equal("Negative quantity.", Assert.Throws<ChrysalisException>(() => client.AddStock(4, -1)).Message);
        }

        [Fact]
        public void FindsOperationsByName()
        {
            var (server, _) = Create();

            Assert.Equal("GetProduct", server.FindOperation("InventoryService.GetProduct")!.Name);
            Assert.Equal("GetProduct", server.FindOperation("InventoryService", "GetProduct")!.Name);
            Assert.Null(server.FindOperation("InventoryService.Missing"));
            Assert.Null(server.FindOperation("NoDot"));
            Assert.Single(server.Services);
        }

        [Fact]
        public void AServiceIsExposedOnce()
        {
            var (server, _) = Create();

            Assert.Throws<InvalidOperationException>(() => server.Expose<IInventoryService>(new InventoryService()));
        }

        [Fact]
        public async Task CallsToServicesThatAreNotExposedFail()
        {
            var operation = ChrysalisRegistry.GetService<IInventoryService>().FindOperation("AddStock")!;

            var exception = await Assert.ThrowsAsync<ChrysalisException>(() =>
                new ChrysalisServer().InvokeAsync(new ChrysalisCallContext(operation, [1, 1], "test")).AsTask());

            Assert.Equal(ChrysalisStatus.Unimplemented, exception.Status);
        }

        [Fact]
        public void PerCallImplementationsAreResolvedForEveryCall()
        {
            var created = 0;
            var server = new ChrysalisServer().Expose<IInventoryService>(_ => { created++; return new InventoryService(); });
            var client = ChrysalisRegistry.CreateClient<IInventoryService>(new InProcessInvoker(server));

            Assert.Equal(1, client.AddStock(1, 1));
            Assert.Equal(1, client.AddStock(1, 1));
            Assert.Equal(2, created);
        }
    }
}

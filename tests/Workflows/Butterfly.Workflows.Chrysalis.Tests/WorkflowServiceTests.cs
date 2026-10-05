using Butterfly.Chrysalis;
using Butterfly.Chrysalis.Binary;
using Butterfly.Chrysalis.Http;
using Butterfly.Chrysalis.Rest;
using Butterfly.Networking.Sockets;
using Butterfly.Workflows.Components;
using Butterfly.Workflows.Runtime;
using Microsoft.Extensions.DependencyInjection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Butterfly.Workflows.Chrysalis.Tests
{
    public class WorkflowServiceTests : IAsyncLifetime
    {
        // The definition the ASP.NET Core API tests use.
        private const string ReviewDefinition = """
            {
              "name": "Review",
              "options": { "allowJumps": true },
              "branches": [
                {
                  "id": "main",
                  "nodes": [
                    {
                      "id": "form",
                      "type": "wait",
                      "pipelines": { "exit": { "validations": [ { "type": "required", "settings": { "paths": [ "event.comment" ] } } ] } }
                    },
                    { "id": "review", "type": "wait" },
                    { "id": "done" }
                  ]
                },
                { "id": "archive", "nodes": [ { "id": "archived", "type": "wait" } ] }
              ],
              "transitions": [
                { "id": "submit", "sourceId": "form", "targetId": "review", "trigger": "event", "event": "submit" },
                { "id": "approve", "sourceId": "review", "targetId": "done", "trigger": "manual" }
              ],
              "events": [ { "id": "order-created", "startsWorkflow": true } ]
            }
            """;

        private ServiceProvider services = null!;
        private ChrysalisBinaryServer binary = null!;
        private HttpServer http = null!;
        private ChrysalisBinaryClient connection = null!;
        private IWorkflowService workflows = null!;

        public async Task InitializeAsync()
        {
            services = new ServiceCollection().AddWorkflows().AddSingleton<WorkflowService>().BuildServiceProvider();
            var chrysalis = new ChrysalisServer().Expose<IWorkflowService>(services.GetRequiredService<WorkflowService>());

            var options = new ChrysalisBinaryServerOptions();
            options.Endpoints.Add((SocketAddress.Loopback(AddressFamily.IPv4, 0), null));
            binary = new ChrysalisBinaryServer(chrysalis, options);
            binary.Start();

            var httpOptions = new HttpServerOptions();
            httpOptions.Endpoints.Add(HttpEndpoint.Loopback(0));
            http = new HttpServer(httpOptions).MapRest(chrysalis);
            http.Start();

            connection = await ChrysalisBinaryClient.ConnectAsync("127.0.0.1", binary.Endpoints[0].Port);
            workflows = connection.CreateClient<IWorkflowService>();
        }

        public async Task DisposeAsync()
        {
            await connection.DisposeAsync();
            await binary.StopAsync();
            await http.StopAsync();
            await services.DisposeAsync();
        }

        private static async Task<ChrysalisStatus> StatusOf(Func<Task> call) => (await Assert.ThrowsAsync<ChrysalisException>(call)).Status;

        [Fact]
        public async Task DefinitionsAreValidatedSavedAndDrawn()
        {
            var saved = await workflows.SaveDefinition("review", JsonNode.Parse(ReviewDefinition)!, CancellationToken.None);
            var found = await workflows.GetDefinition("review", null, CancellationToken.None);
            var mermaid = await workflows.GetDefinitionMermaid("review", null, CancellationToken.None);
            var validation = await workflows.ValidateDefinition(JsonNode.Parse("""{ "id": "empty" }""")!, CancellationToken.None);

            Assert.Equal("review", saved["id"]!.GetValue<string>());
            Assert.Equal("manual", found["transitions"]![1]!["trigger"]!.GetValue<string>());
            Assert.StartsWith("flowchart TD", mermaid);
            Assert.False(validation.Valid);
            Assert.NotEmpty(validation.Errors);

            Assert.Equal(ChrysalisStatus.InvalidArgument, await StatusOf(() => workflows.SaveDefinition("broken", JsonNode.Parse(
                """{ "branches": [ { "id": "main", "nodes": [ { "id": "a" } ] } ], "transitions": [ { "id": "t", "sourceId": "a", "targetId": "nowhere" } ] }""")!, CancellationToken.None)));
            Assert.Equal(ChrysalisStatus.InvalidArgument, await StatusOf(() => workflows.SaveDefinition("other", JsonNode.Parse("""{ "id": "review" }""")!, CancellationToken.None)));
            Assert.Equal(ChrysalisStatus.NotFound, await StatusOf(() => workflows.GetDefinition("missing", null, CancellationToken.None)));
        }

        [Fact]
        public async Task InstancesMoveThroughTheService()
        {
            await workflows.SaveDefinition("review", JsonNode.Parse(ReviewDefinition)!, CancellationToken.None);

            var started = await workflows.StartInstance("review", new WorkflowStart(Variables: new JsonObject { ["amount"] = 10 }), CancellationToken.None);
            var id = started["id"]!.GetValue<string>();
            Assert.Equal("running", started["status"]!.GetValue<string>());
            Assert.Equal(10, started["variables"]!["amount"]!.GetValue<long>());

            Assert.Equal(ChrysalisStatus.Aborted, await StatusOf(() => workflows.PublishEvent(id, new WorkflowEventMessage("unknown"), CancellationToken.None)));
            var rejected = await Assert.ThrowsAsync<ChrysalisException>(() => workflows.PublishEvent(id, new WorkflowEventMessage("submit", new JsonObject()), CancellationToken.None));
            Assert.Equal(ChrysalisStatus.FailedPrecondition, rejected.Status);
            Assert.Contains("'event.comment' is required.", rejected.Message);

            var submitted = await workflows.PublishEvent(id, new WorkflowEventMessage("submit", new JsonObject { ["comment"] = "ok" }), CancellationToken.None);
            Assert.Equal("review", submitted["tokens"]![0]!["nodeId"]!.GetValue<string>());

            var graph = await workflows.GetInstanceGraph(id, CancellationToken.None);
            Assert.Contains(graph["nodes"]!.AsArray(), node => node!["id"]!.GetValue<string>() == "review" && node["state"]!.GetValue<string>() == "waiting");

            var approved = await workflows.TriggerTransition(id, "approve", null, CancellationToken.None);
            Assert.Equal("completed", approved["status"]!.GetValue<string>());

            var completed = await workflows.QueryInstances("review", WorkflowStatus.Completed, null, null, null, null, CancellationToken.None);
            Assert.Equal(id, Assert.Single(completed.AsArray())!["id"]!.GetValue<string>());
        }

        [Fact]
        public async Task DispatchesEventsAndListsComponents()
        {
            await workflows.SaveDefinition("review", JsonNode.Parse(ReviewDefinition)!, CancellationToken.None);

            var results = await workflows.Dispatch(new WorkflowEventMessage("order-created", CorrelationId: "order-1"), CancellationToken.None);
            var steps = workflows.ListComponents(WorkflowComponentKind.Validator);

            Assert.Equal(WorkflowOperationStatus.Succeeded, Assert.Single(results).Status);
            Assert.Equal("order-1", results[0].Instance!["correlationId"]!.GetValue<string>());
            Assert.Contains(steps, component => component.Type == "required");
            Assert.All(steps, component => Assert.Equal(WorkflowComponentKind.Validator, component.Kind));
        }

        [Fact]
        public async Task SpeaksRestLikeTheAspNetCoreEndpoints()
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{http.Endpoints[0].Address.Port}/") };

            var saved = await client.PutAsync("/api/workflows/definitions/review", new StringContent(ReviewDefinition, Encoding.UTF8, "application/json"));
            var started = await client.PostAsync("/api/workflows/definitions/review/instances", new StringContent("""{"variables":{"amount":5}}""", Encoding.UTF8, "application/json"));
            var missing = await client.GetAsync("/api/workflows/instances/nope");

            Assert.Equal(System.Net.HttpStatusCode.OK, saved.StatusCode);
            using var instance = JsonDocument.Parse(await started.Content.ReadAsStringAsync());
            Assert.Equal("running", instance.RootElement.GetProperty("status").GetString());
            Assert.Equal(System.Net.HttpStatusCode.NotFound, missing.StatusCode);
        }
    }
}

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Butterfly.Workflows.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Butterfly.Workflows.Tests
{
    public sealed class HttpApiTests : IAsyncLifetime
    {
        const string ReviewDefinition = """
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

        WebApplication _app = null!;
        HttpClient _client = null!;

        public async Task InitializeAsync()
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddWorkflows(workflows => workflows.AddComponentsFromAssembly(typeof(WorkflowTestHost).Assembly));
            builder.Services.AddWorkflowProblemDetails();

            _app = builder.Build();
            _app.UseExceptionHandler();
            _app.MapWorkflowEndpoints();
            await _app.StartAsync();
            _client = _app.GetTestClient();
        }

        public async Task DisposeAsync()
        {
            _client.Dispose();
            await _app.DisposeAsync();
        }

        [Fact]
        public async Task DefinitionsAreValidatedSavedAndDrawn()
        {
            var saved = await SendAsync(HttpMethod.Put, "/workflows/definitions/review", ReviewDefinition);
            Assert.Equal(HttpStatusCode.OK, saved.Status);
            Assert.Equal("review", saved.Json!["id"]!.GetValue<string>());

            var found = await SendAsync(HttpMethod.Get, "/workflows/definitions/review");
            Assert.Equal("manual", found.Json!["transitions"]![1]!["trigger"]!.GetValue<string>());

            var broken = await SendAsync(HttpMethod.Put, "/workflows/definitions/broken",
                """{ "branches": [ { "id": "main", "nodes": [ { "id": "a" } ] } ], "transitions": [ { "id": "t", "sourceId": "a", "targetId": "nowhere" } ] }""");
            Assert.Equal(HttpStatusCode.BadRequest, broken.Status);
            Assert.Equal("transition.target.notFound", broken.Json!["errors"]![0]!["code"]!.GetValue<string>());

            var mismatch = await SendAsync(HttpMethod.Put, "/workflows/definitions/other", """{ "id": "review", "branches": [ { "id": "main" } ] }""");
            Assert.Equal(HttpStatusCode.BadRequest, mismatch.Status);

            var notJson = await SendAsync(HttpMethod.Put, "/workflows/definitions/review", "{ not json");
            Assert.Equal(HttpStatusCode.BadRequest, notJson.Status);

            var validation = await SendAsync(HttpMethod.Post, "/workflows/definitions/validate", """{ "id": "empty" }""");
            Assert.False(validation.Json!["valid"]!.GetValue<bool>());

            var mermaid = await _client.GetStringAsync("/workflows/definitions/review/mermaid");
            Assert.StartsWith("flowchart TD", mermaid);

            Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, "/workflows/definitions/missing")).Status);
        }

        [Fact]
        public async Task InstancesMoveThroughTheApi()
        {
            await SendAsync(HttpMethod.Put, "/workflows/definitions/review", ReviewDefinition);

            var started = await SendAsync(HttpMethod.Post, "/workflows/definitions/review/instances", """{ "variables": { "amount": 10 } }""");
            Assert.Equal(HttpStatusCode.Created, started.Status);
            var id = started.Json!["id"]!.GetValue<string>();
            Assert.Equal($"/workflows/instances/{id}", started.Location);
            Assert.Equal("running", started.Json["status"]!.GetValue<string>());

            Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(HttpMethod.Post, $"/workflows/instances/{id}/events", """{ "name": "unknown" }""")).Status);

            var rejected = await SendAsync(HttpMethod.Post, $"/workflows/instances/{id}/events", """{ "name": "submit", "payload": {} }""");
            Assert.Equal((HttpStatusCode)422, rejected.Status);
            Assert.Equal("'event.comment' is required.", rejected.Json!["errors"]![0]!["message"]!.GetValue<string>());

            var submitted = await SendAsync(HttpMethod.Post, $"/workflows/instances/{id}/events", """{ "name": "submit", "payload": { "comment": "ok" } }""");
            Assert.Equal(HttpStatusCode.OK, submitted.Status);
            Assert.Equal("review", submitted.Json!["tokens"]![0]!["nodeId"]!.GetValue<string>());

            var graph = await SendAsync(HttpMethod.Get, $"/workflows/instances/{id}/graph");
            Assert.Contains(graph.Json!["nodes"]!.AsArray(), node => node!["id"]!.GetValue<string>() == "review" && node["state"]!.GetValue<string>() == "waiting");

            var approved = await SendAsync(HttpMethod.Post, $"/workflows/instances/{id}/transitions/approve");
            Assert.Equal("completed", approved.Json!["status"]!.GetValue<string>());

            var completed = await SendAsync(HttpMethod.Get, "/workflows/instances?status=Completed&definitionId=review");
            Assert.Equal(id, Assert.Single(completed.Json!.AsArray())!["id"]!.GetValue<string>());
            Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Get, "/workflows/instances?status=bogus")).Status);

            Assert.Equal((HttpStatusCode)422, (await SendAsync(HttpMethod.Post, $"/workflows/instances/{id}/cancel", """{ "reason": "too late" }""")).Status);
            Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, "/workflows/instances/missing")).Status);

            var unknownInstance = await SendAsync(HttpMethod.Post, "/workflows/instances/missing/events", """{ "name": "submit" }""");
            Assert.Equal(HttpStatusCode.NotFound, unknownInstance.Status);
            Assert.Equal("Workflow not found", unknownInstance.Json!["title"]!.GetValue<string>());
        }

        [Fact]
        public async Task JumpsDispatchAndComponentsAreExposed()
        {
            await SendAsync(HttpMethod.Put, "/workflows/definitions/review", ReviewDefinition);

            var dispatched = await SendAsync(HttpMethod.Post, "/workflows/events", """{ "name": "order-created", "payload": { "orderId": 7 }, "correlationId": "order-7" }""");
            var result = Assert.Single(dispatched.Json!.AsArray())!;
            Assert.Equal("succeeded", result["status"]!.GetValue<string>());
            var id = result["instance"]!["id"]!.GetValue<string>();

            // Jumps still leave the node through its exit validations, which require a comment.
            var blocked = await SendAsync(HttpMethod.Post, $"/workflows/instances/{id}/jump", """{ "targetId": "archived" }""");
            Assert.Equal((HttpStatusCode)422, blocked.Status);

            var jumped = await SendAsync(HttpMethod.Post, $"/workflows/instances/{id}/jump", """{ "targetId": "archived", "payload": { "comment": "moved by an operator" } }""");
            Assert.Equal(HttpStatusCode.OK, jumped.Status);
            Assert.Equal("archive", jumped.Json!["tokens"]![0]!["branchId"]!.GetValue<string>());

            var cancelled = await SendAsync(HttpMethod.Post, $"/workflows/instances/{id}/cancel");
            Assert.Equal("cancelled", cancelled.Json!["status"]!.GetValue<string>());

            var nodeTypes = await SendAsync(HttpMethod.Get, "/workflows/components?kind=nodeType");
            var wait = Assert.Single(nodeTypes.Json!.AsArray(), component => component!["type"]!.GetValue<string>() == "wait")!;
            Assert.Contains("event", wait["settingsSchema"]!.ToJsonString());
            Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Get, "/workflows/components?kind=bogus")).Status);
        }

        async Task<(HttpStatusCode Status, JsonNode? Json, string? Location)> SendAsync(HttpMethod method, string url, string? json = null)
        {
            using var request = new HttpRequestMessage(method, url);
            if (json is not null)
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            else if (method != HttpMethod.Get)
                request.Content = new ByteArrayContent([]);

            using var response = await _client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            JsonNode? parsed = null;
            if (body.Length > 0 && response.Content.Headers.ContentType?.MediaType?.Contains("json") == true)
                parsed = JsonNode.Parse(body);
            return (response.StatusCode, parsed, response.Headers.Location?.OriginalString);
        }
    }
}

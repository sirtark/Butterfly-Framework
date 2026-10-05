using System.Text.Json.Nodes;
using Butterfly.Workflows.Components;
using Butterfly.Workflows.Definitions;
using Butterfly.Workflows.Graph;
using Butterfly.Workflows.Persistence;
using Butterfly.Workflows.Runtime;
using Butterfly.Workflows.Serialization;
using Microsoft.Extensions.DependencyInjection;
using static Butterfly.Workflows.Tests.WorkflowTestHost;

namespace Butterfly.Workflows.Tests
{
    public sealed class DefinitionAndPersistenceTests
    {
        static WorkflowDefinition Approvals()
            => new WorkflowDefinitionBuilder("approvals")
                .Named("Approvals", "Legal and finance review")
                .WithMetadata("owner", "back-office")
                .AddBranch("main", branch => branch
                    .At(0, 0, 800, 400)
                    .AddNode("approvals", node => node
                        .At(40, 40)
                        .AddBranch("legal", legal => legal.AddNode("legal-review", review => review.Named("Legal review").OfType("wait", new { @event = "legal-ok" })))
                        .AddBranch("finance", finance => finance.AddNode("finance-review", review => review.OfType("wait", new { @event = "finance-ok" }))))
                    .AddNode("done", node => node.OnEnter(pipeline => pipeline.Step("log", new { message = "Approved {variables.amount}" }))))
                .AddTransition("approvals", "done", transition => transition.Named("approved"))
                .Build();

        [Fact]
        public async Task ValidatorReportsStructuralErrors()
        {
            var definition = new WorkflowDefinition
            {
                Id = "broken",
                Branches =
                [
                    new BranchDefinition
                    {
                        Id = "main",
                        StartNodeId = "missing",
                        Nodes = [new NodeDefinition { Id = "a", Type = "teleport" }, new NodeDefinition { Id = "a" }]
                    },
                    new BranchDefinition { Id = "vault", AllowExternalEntry = false, Nodes = [new NodeDefinition { Id = "gold" }, new NodeDefinition { Id = "$workflow" }] }
                ],
                Transitions =
                [
                    new TransitionDefinition { Id = "to-nowhere", SourceId = "a", TargetId = "nowhere" },
                    new TransitionDefinition { Id = "into-vault", SourceId = "a", TargetId = "gold" },
                    new TransitionDefinition { Id = "nameless", SourceId = "a", TargetId = "a", Trigger = TransitionTrigger.Event }
                ]
            };
            var services = CreateServices();

            var codes = services.GetRequiredService<WorkflowDefinitionValidator>().Validate(definition).Select(error => error.Code).ToList();

            Assert.Contains("branch.start.notFound", codes);
            Assert.Contains("component.type.unknown", codes);
            Assert.Contains("element.id.duplicate", codes);
            Assert.Contains("transition.target.notFound", codes);
            Assert.Contains("transition.entry.denied", codes);
            Assert.Contains("transition.event.required", codes);
            Assert.Contains("element.id.reserved", codes);
            var exception = await Assert.ThrowsAsync<WorkflowDefinitionException>(() => services.GetRequiredService<IWorkflowEngine>().StartAsync(definition));
            Assert.NotEmpty(exception.Errors);
        }

        [Fact]
        public void DefinitionsRoundTripThroughCompactJson()
        {
            var json = WorkflowJson.Serialize(Approvals(), indented: true);
            var copy = WorkflowJson.Deserialize<WorkflowDefinition>(json);

            Assert.Equal(json, WorkflowJson.Serialize(copy, indented: true));
            Assert.Contains("\"type\": \"wait\"", json);
            Assert.Contains("\"owner\": \"back-office\"", json);
            Assert.DoesNotContain("\"branches\": []", json);
            Assert.DoesNotContain("\"description\": null", json);
            Assert.Equal("legal-ok", copy.Branches[0].Nodes[0].Branches[0].Nodes[0].Settings!["event"]!.GetValue<string>());
        }

        [Fact]
        public async Task InstancesPersistAndResumeOnAnotherEngine()
        {
            var definitions = new InMemoryWorkflowDefinitionStore();
            var instances = new InMemoryWorkflowInstanceStore();
            IWorkflowEngine NewEngine() => CreateServices(registerServices: services =>
            {
                services.AddSingleton<IWorkflowDefinitionStore>(definitions);
                services.AddSingleton<IWorkflowInstanceStore>(instances);
            }).GetRequiredService<IWorkflowEngine>();

            var started = (await NewEngine().StartAsync(Approvals(), new() { Variables = new JsonObject { ["amount"] = 1200 }, CorrelationId = "po-1" })).Succeeded();
            var stored = (await instances.FindAsync(started.Id))!;
            Assert.Equal(WorkflowJson.Serialize(started), WorkflowJson.Serialize(stored));

            var waitingAtLegal = await instances.QueryAsync(new() { ElementId = "legal-review" });
            Assert.Equal(started.Id, Assert.Single(waitingAtLegal).Id);

            var resumed = NewEngine();
            (await resumed.PublishAsync(started.Id, new WorkflowEvent("legal-ok"))).Succeeded();
            var completed = (await resumed.PublishAsync(started.Id, new WorkflowEvent("finance-ok"))).Succeeded();

            Assert.Equal(WorkflowStatus.Completed, completed.Status);
            Assert.Equal(3, completed.Revision);
            Assert.Empty(await instances.QueryAsync(new() { Status = WorkflowStatus.Running }));
        }

        [Fact]
        public async Task StaleSavesAreRejected()
        {
            var instances = new InMemoryWorkflowInstanceStore();
            await instances.SaveAsync(new WorkflowInstance { Id = "one" });

            var first = (await instances.FindAsync("one"))!;
            var second = (await instances.FindAsync("one"))!;
            await instances.SaveAsync(first);

            var conflict = await Assert.ThrowsAsync<WorkflowConcurrencyException>(() => instances.SaveAsync(second).AsTask());
            Assert.Equal(1, conflict.ExpectedRevision);
            Assert.Equal(2, conflict.ActualRevision);
        }

        [Fact]
        public async Task CustomComponentsPlugInAndReplaceBuiltIns()
        {
            var services = CreateServices(builder => builder.AddNodeType<StampingTaskNodeType>("task"));
            var definition = new WorkflowDefinitionBuilder("purchase")
                .AddBranch("main", branch => branch
                    .AddNode("approve", node => node.OfType("approval", new { approvers = new[] { "ana", "luis" } }))
                    .AddNode("register"))
                .AddTransition("approve", "register")
                .Build();
            var engine = services.GetRequiredService<IWorkflowEngine>();
            var started = (await engine.StartAsync(definition)).Succeeded();

            var ignored = await engine.PublishAsync(started.Id, new WorkflowEvent("approve", new JsonObject { ["approver"] = "mallory" }));
            var approved = (await engine.PublishAsync(started.Id, new WorkflowEvent("approve", new JsonObject { ["approver"] = "luis" }))).Succeeded();

            Assert.Equal(WorkflowOperationStatus.NotHandled, ignored.Status);
            Assert.Equal(WorkflowStatus.Completed, approved.Status);
            Assert.Equal("luis", approved.Outputs["approve"]!["approver"]!.GetValue<string>());
            Assert.Equal("register", approved.Variables["stamped"]!.GetValue<string>());

            var descriptor = services.GetRequiredService<WorkflowComponentCatalog>().Get(WorkflowComponentKind.NodeType, "approval");
            Assert.Equal("Approval", descriptor.DisplayName);
            Assert.Contains("approvers", descriptor.GetSettingsSchema()!.ToJsonString());
        }

        [Fact]
        public async Task GraphsShowStructureAndLiveState()
        {
            var engine = CreateEngine();
            var definition = Approvals();
            var instance = (await engine.StartAsync(definition)).Succeeded();
            instance = (await engine.PublishAsync(instance.Id, new WorkflowEvent("legal-ok"))).Succeeded();

            var graph = WorkflowGraph.Create(definition, instance);

            Assert.Equal(WorkflowGraphNodeKind.Workflow, graph.Nodes[0].Kind);
            Assert.Equal("approvals", graph.Nodes.Single(node => node.Id == "legal").ParentId);
            Assert.True(graph.Nodes.Single(node => node.Id == "main").IsStart);
            Assert.Equal(WorkflowGraphNodeState.WaitingForChildren, graph.Nodes.Single(node => node.Id == "approvals").State);
            Assert.Equal(WorkflowGraphNodeState.Visited, graph.Nodes.Single(node => node.Id == "legal-review").State);
            Assert.Equal(WorkflowGraphNodeState.Waiting, graph.Nodes.Single(node => node.Id == "finance-review").State);
            Assert.Equal(WorkflowGraphNodeState.Idle, graph.Nodes.Single(node => node.Id == "done").State);
            Assert.Equal("approved", Assert.Single(graph.Edges).Label);

            var mermaid = graph.ToMermaid();
            Assert.Contains("subgraph n_approvals[\"approvals\"]", mermaid);
            Assert.Contains("n_finance_review[\"finance-review · wait\"]", mermaid);
            Assert.Contains("n_approvals -->|\"approved\"| n_done", mermaid);
            Assert.Contains("style n_finance_review fill:#fef3c7", mermaid);
        }
    }

    public sealed class ApprovalSettings
    {
        public List<string> Approvers { get; set; } = [];
    }

    [WorkflowComponent("approval", DisplayName = "Approval", Category = "Human", SettingsType = typeof(ApprovalSettings))]
    public sealed class ApprovalNodeType : IWorkflowNodeType
    {
        public ValueTask<NodeResult> ExecuteAsync(WorkflowContext context) => ValueTask.FromResult(NodeResult.Wait);

        public ValueTask<NodeResult> HandleEventAsync(WorkflowContext context)
        {
            var approver = context.GetValue("event.approver")?.GetValue<string>();
            if (context.Event?.Name != "approve" || approver is null || !context.GetSettings<ApprovalSettings>().Approvers.Contains(approver))
                return ValueTask.FromResult(NodeResult.Ignore);

            context.SetOutput(new JsonObject { ["approver"] = approver });
            return ValueTask.FromResult(NodeResult.Complete);
        }
    }

    public sealed class StampingTaskNodeType : IWorkflowNodeType
    {
        public ValueTask<NodeResult> ExecuteAsync(WorkflowContext context)
        {
            context.Variables["stamped"] = context.Element.Id;
            return ValueTask.FromResult(NodeResult.Complete);
        }
    }
}

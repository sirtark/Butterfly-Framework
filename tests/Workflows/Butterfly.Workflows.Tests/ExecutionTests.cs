using System.Text.Json.Nodes;
using Butterfly.Workflows.Definitions;
using Butterfly.Workflows.Persistence;
using Butterfly.Workflows.Runtime;
using Microsoft.Extensions.DependencyInjection;
using static Butterfly.Workflows.Tests.WorkflowTestHost;

namespace Butterfly.Workflows.Tests
{
    public sealed class ExecutionTests
    {
        [Fact]
        public async Task LinearWorkflowRunsPipelinesInOrderAndProducesOutput()
        {
            var definition = new WorkflowDefinitionBuilder("linear")
                .WithVariables(new { amount = 10 })
                .WithOutput("variables", new { names = new[] { "amount", "status" } })
                .AddBranch("main", branch => branch
                    .AddNode("prepare", node => node.OnExit(pipeline => pipeline.Step("set-variables", new { values = new { status = "ready" } })))
                    .AddNode("finish"))
                .AddTransition("prepare", "finish")
                .Build()
                .Traced();

            var instance = (await CreateEngine().StartAsync(definition)).Succeeded();

            Assert.Equal(WorkflowStatus.Completed, instance.Status);
            Assert.Empty(instance.Tokens);
            Assert.Equal("""{"amount":10,"status":"ready"}""", instance.Output!.ToJsonString());
            Assert.Equal(
                ["enter:linear", "enter:main", "enter:prepare", "exit:prepare", "transition:prepare->finish", "enter:finish", "exit:finish", "exit:main", "exit:linear"],
                TraceOf(instance));
        }

        [Fact]
        public async Task EventTransitionsMoveWaitingTokensAndUnknownEventsAreNotHandled()
        {
            var definition = new WorkflowDefinitionBuilder("approval")
                .AddBranch("main", branch => branch
                    .AddNode("request", node => node.OfType("wait"))
                    .AddNode("approved")
                    .AddNode("rejected"))
                .AddTransition("request", "approved", transition => transition.OnEvent("approve"))
                .AddTransition("request", "rejected", transition => transition.OnEvent("reject"))
                .Build();
            var engine = CreateEngine();

            var started = (await engine.StartAsync(definition)).Succeeded();
            Assert.Equal("request", started.SingleToken().NodeId);
            Assert.Equal(WorkflowTokenStatus.Waiting, started.SingleToken().Status);

            var ignored = await engine.PublishAsync(started.Id, new WorkflowEvent("unknown"));
            Assert.Equal(WorkflowOperationStatus.NotHandled, ignored.Status);
            Assert.Equal(started.Revision, ignored.Instance!.Revision);

            var approved = (await engine.PublishAsync(started.Id, new WorkflowEvent("approve"))).Succeeded();
            Assert.Equal(WorkflowStatus.Completed, approved.Status);
            Assert.Contains(approved.History, entry => entry.Kind == WorkflowHistoryKind.TransitionTaken && entry.ElementId == "request->approved");
            Assert.DoesNotContain(approved.History, entry => entry.ElementId == "rejected");
        }

        [Theory]
        [InlineData(5000, "manager")]
        [InlineData(10, "automatic")]
        public async Task ConditionsChooseTheAutomaticTransition(int amount, string expectedNode)
        {
            var definition = new WorkflowDefinitionBuilder("purchase")
                .AddBranch("main", branch => branch
                    .AddNode("check")
                    .AddNode("manager", node => node.OfType("end"))
                    .AddNode("automatic", node => node.OfType("end")))
                .AddTransition("check", "manager", transition => transition
                    .WithPriority(1)
                    .When("compare", new { path = "variables.amount", @operator = "greaterThan", value = 1000 }))
                .AddTransition("check", "automatic")
                .Build();

            var instance = (await CreateEngine().StartAsync(definition, new() { Variables = new JsonObject { ["amount"] = amount } })).Succeeded();

            Assert.Equal(WorkflowStatus.Completed, instance.Status);
            var entered = instance.History.Where(entry => entry.Kind == WorkflowHistoryKind.NodeEntered).Select(entry => entry.ElementId).ToList();
            Assert.Equal(["check", expectedNode], entered);
        }

        [Fact]
        public async Task ValidationFailureRejectsTheEventAndLeavesTheInstanceUntouched()
        {
            var definition = new WorkflowDefinitionBuilder("form")
                .AddBranch("main", branch => branch
                    .AddNode("fill", node => node
                        .OfType("wait")
                        .OnExit(pipeline => pipeline.Validate("required", new { paths = new[] { "event.comment" } })))
                    .AddNode("done"))
                .AddTransition("fill", "done", transition => transition.OnEvent("submit"))
                .Build();
            var engine = CreateEngine();
            var started = (await engine.StartAsync(definition)).Succeeded();

            var rejected = await engine.PublishAsync(started.Id, new WorkflowEvent("submit", new JsonObject()));

            Assert.Equal(WorkflowOperationStatus.Rejected, rejected.Status);
            var error = Assert.Single(rejected.Errors);
            Assert.Equal("'event.comment' is required.", error.Message);
            Assert.Equal("fill", error.ElementId);
            Assert.Equal(started.Revision, rejected.Instance!.Revision);
            Assert.Equal("fill", rejected.Instance.SingleToken().NodeId);
            Assert.DoesNotContain(rejected.Instance.History, entry => entry.Kind == WorkflowHistoryKind.EventReceived);

            var accepted = (await engine.PublishAsync(started.Id, new WorkflowEvent("submit", new JsonObject { ["comment"] = "ok" }))).Succeeded();
            Assert.Equal(WorkflowStatus.Completed, accepted.Status);
        }

        [Fact]
        public async Task StrictEventsRejectUndeclaredEventsAndEventPipelinesValidatePayloads()
        {
            var definition = new WorkflowDefinitionBuilder("strict")
                .Configure(options => options.StrictEvents = true)
                .AddEvent("pay", workflowEvent => workflowEvent.WithPipeline(pipeline => pipeline
                    .Validate("compare", new { path = "event.amount", @operator = "greaterThan", value = 0 }, "The amount must be positive.")
                    .Step("set-variables", new { from = new { paid = "event.amount" } })))
                .AddBranch("main", branch => branch
                    .AddNode("waiting", node => node.OfType("wait"))
                    .AddNode("paid"))
                .AddTransition("waiting", "paid", transition => transition.OnEvent("pay"))
                .Build();
            var engine = CreateEngine();
            var started = (await engine.StartAsync(definition)).Succeeded();

            var undeclared = await engine.PublishAsync(started.Id, new WorkflowEvent("refund"));
            Assert.Equal(WorkflowOperationStatus.Rejected, undeclared.Status);

            var invalid = await engine.PublishAsync(started.Id, new WorkflowEvent("pay", new JsonObject { ["amount"] = -5 }));
            Assert.Equal(WorkflowOperationStatus.Rejected, invalid.Status);
            Assert.Equal("The amount must be positive.", Assert.Single(invalid.Errors).Message);

            var paid = (await engine.PublishAsync(started.Id, new WorkflowEvent("pay", new JsonObject { ["amount"] = 30 }))).Succeeded();
            Assert.Equal(WorkflowStatus.Completed, paid.Status);
            Assert.Equal(30, paid.Variables["paid"]!.GetValue<int>());
        }

        [Fact]
        public async Task StepExceptionsFaultTheInstanceAndRunFaultPipelines()
        {
            var definition = new WorkflowDefinitionBuilder("faulty")
                .OnFault(pipeline => pipeline.Step("set-variables", new { values = new { compensated = true } }))
                .AddBranch("main", branch => branch
                    .AddNode("tolerant", node => node.OnEnter(pipeline => pipeline.Step("fail", new { message = "ignored" }, step => step.ContinueOnError())))
                    .AddNode("broken", node => node.OnEnter(pipeline => pipeline.Step("fail", new { message = "boom at {variables.stage}" }))))
                .WithVariables(new { stage = "billing" })
                .AddTransition("tolerant", "broken")
                .Build();

            var result = await CreateEngine().StartAsync(definition);

            Assert.Equal(WorkflowOperationStatus.Faulted, result.Status);
            var instance = result.Instance!;
            Assert.Equal(WorkflowStatus.Faulted, instance.Status);
            Assert.Equal("broken", instance.Fault!.ElementId);
            Assert.Equal("enter", instance.Fault.Stage);
            Assert.Equal("fail", instance.Fault.ComponentType);
            Assert.Equal("boom at billing", instance.Fault.Message);
            Assert.Contains(instance.History, entry => entry.Kind == WorkflowHistoryKind.StepFailed && entry.ElementId == "tolerant");
            Assert.True(instance.Variables["compensated"]!.GetValue<bool>());
            Assert.Equal(1, instance.Revision);
        }

        [Fact]
        public async Task EndlessAutomaticLoopsFaultInsteadOfHanging()
        {
            var definition = new WorkflowDefinitionBuilder("loop")
                .AddBranch("main", branch => branch.AddNode("ping").AddNode("pong"))
                .AddTransition("ping", "pong")
                .AddTransition("pong", "ping")
                .Build();

            var result = await CreateEngine(builder => builder.Configure(options => options.MaxActivationsPerOperation = 100)).StartAsync(definition);

            Assert.Equal(WorkflowOperationStatus.Faulted, result.Status);
            Assert.Contains("activations", result.Instance!.Fault!.Message);
        }

        [Fact]
        public async Task ManualTransitionsAndJumpsAreConfigurable()
        {
            var definition = new WorkflowDefinitionBuilder("document")
                .AddBranch("editing", branch => branch
                    .AddNode("draft", node => node.OfType("wait"))
                    .AddNode("review", node => node.OfType("wait")))
                .AddBranch("archive", branch => branch
                    .DenyExternalEntry()
                    .AddNode("archived", node => node.OfType("wait")))
                .AddBranch("publishing", branch => branch.AddNode("published", node => node.OfType("wait")))
                .AddTransition("draft", "review", transition => transition.WithId("send-to-review").Manual())
                .Build();
            var engine = CreateEngine();
            var started = (await engine.StartAsync(definition)).Succeeded();

            Assert.Equal(WorkflowOperationStatus.Rejected, (await engine.JumpAsync(started.Id, "published")).Status);

            var reviewing = (await engine.TriggerTransitionAsync(started.Id, "send-to-review")).Succeeded();
            Assert.Equal("review", reviewing.SingleToken().NodeId);

            definition.Version = 2;
            definition.Options.AllowJumps = true;
            var second = (await engine.StartAsync(definition)).Succeeded();

            var denied = await engine.JumpAsync(second.Id, "archived");
            Assert.Equal(WorkflowOperationStatus.Rejected, denied.Status);
            Assert.Contains("archive", denied.Errors[0].Message);

            var jumped = (await engine.JumpAsync(second.Id, "published")).Succeeded();
            Assert.Equal("published", jumped.SingleToken().NodeId);
            Assert.Equal("publishing", jumped.SingleToken().BranchId);
            Assert.Contains(jumped.History, entry => entry.Kind == WorkflowHistoryKind.Jumped && entry.ElementId == "published");
        }

        [Fact]
        public async Task DispatchStartsWorkflowsAndDeliversCorrelatedEvents()
        {
            var definition = new WorkflowDefinitionBuilder("order")
                .AddEvent("order-created", workflowEvent => workflowEvent.StartsWorkflow())
                .AddEvent("order-paid")
                .AddBranch("main", branch => branch
                    .AddNode("awaiting-payment", node => node.OfType("wait"))
                    .AddNode("fulfil"))
                .AddTransition("awaiting-payment", "fulfil", transition => transition.OnEvent("order-paid"))
                .Build();
            var services = CreateServices();
            var engine = services.GetRequiredService<IWorkflowEngine>();
            await services.GetRequiredService<IWorkflowDefinitionStore>().SaveAsync(definition);

            var created = await engine.DispatchAsync(new WorkflowEvent("order-created", new JsonObject { ["orderId"] = 7 }) { CorrelationId = "order-7" });
            var started = Assert.Single(created).Succeeded();
            Assert.Equal(7, started.Input!["orderId"]!.GetValue<int>());

            var paid = await engine.DispatchAsync(new WorkflowEvent("order-paid") { CorrelationId = "order-7" });
            Assert.Equal(WorkflowStatus.Completed, Assert.Single(paid).Succeeded().Status);
        }
    }
}

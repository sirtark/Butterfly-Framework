using System.Text.Json.Nodes;
using Butterfly.Scripting;
using Butterfly.Scripting.CSharp;
using Butterfly.Scripting.Lua;
using Butterfly.Scripting.Lua.MoonSharp;
using Butterfly.Workflows.Components;
using Butterfly.Workflows.Definitions;
using Butterfly.Workflows.Runtime;
using Butterfly.Workflows.Scripting;
using Microsoft.Extensions.DependencyInjection;
using static Butterfly.Workflows.Tests.WorkflowTestHost;

namespace Butterfly.Workflows.Tests
{
    public sealed class ScriptingTests
    {
        static ServiceProvider CreateScriptingServices()
            => CreateServices(
                workflows => workflows.AddScripting(options => options.DefaultLanguage = ScriptingLanguage.Lua),
                services =>
                {
                    services.AddScriptInvoker(invoker => invoker.AddLua(lua => lua.UseMoonSharp()));
                    services.AddKeyedScriptInvoker("csharp", invoker => invoker.AddCSharp());
                });

        [Fact]
        public async Task ScriptsDriveStepsConditionsValidationsAndOutputs()
        {
            var definition = new WorkflowDefinitionBuilder("scripted-purchase")
                .WithVariables(new { items = new object[] { new { price = 10.5, qty = 2 }, new { price = 100, qty = 1 } } })
                .AddEvent("approve", workflowEvent => workflowEvent.WithPipeline(pipeline => pipeline
                    .Validate("script", new { source = "if not event.approver then return 'An approver is required.' end" })))
                .AddBranch("main", branch => branch
                    .AddNode("total", node => node.OnEnter(pipeline => pipeline.Step("script", new
                    {
                        source = "local sum = 0 for _, item in ipairs(variables.items) do sum = sum + item.price * item.qty end return { total = sum, lines = #variables.items }",
                        merge = true
                    })))
                    .AddNode("manager", node => node.OfType("wait"))
                    .AddNode("automatic")
                    .AddNode("summary", node => node.WithOutput("script", new { source = "return { total = variables.total, approvedBy = variables.approvedBy or 'system' }" })))
                .AddTransition("total", "manager", transition => transition
                    .WithPriority(1)
                    .When("script", new { source = "return total > 100", parameters = new { total = "variables.total" } }))
                .AddTransition("total", "automatic")
                .AddTransition("manager", "summary", transition => transition
                    .OnEvent("approve")
                    .WithPipeline(pipeline => pipeline.Step("script", new { source = "return event.approver", assignTo = "approvedBy" })))
                .AddTransition("automatic", "summary")
                .Build();
            var engine = CreateScriptingServices().GetRequiredService<IWorkflowEngine>();

            var started = (await engine.StartAsync(definition)).Succeeded();
            Assert.Equal("121", started.Variables["total"]!.ToJsonString());
            Assert.Equal("2", started.Variables["lines"]!.ToJsonString());
            Assert.Equal("manager", started.SingleToken().NodeId);

            var rejected = await engine.PublishAsync(started.Id, new WorkflowEvent("approve", new JsonObject()));
            Assert.Equal(WorkflowOperationStatus.Rejected, rejected.Status);
            Assert.Equal("An approver is required.", Assert.Single(rejected.Errors).Message);

            var approved = (await engine.PublishAsync(started.Id, new WorkflowEvent("approve", new JsonObject { ["approver"] = "ana" }))).Succeeded();
            Assert.Equal(WorkflowStatus.Completed, approved.Status);
            Assert.Equal("""{"total":121,"approvedBy":"ana"}""", approved.Outputs["summary"]!.ToJsonString());
        }

        [Fact]
        public async Task ScriptNodesDecideWhetherToWaitAndHowToHandleEvents()
        {
            var definition = new WorkflowDefinitionBuilder("scripted-node")
                .WithVariables(new { a = 21, ready = false })
                .AddBranch("main", branch => branch
                    .AddNode("compute", node => node.OfType("script", new { source = "return variables.a * 2" }))
                    .AddNode("decide", node => node.OfType("script", new
                    {
                        source = "if variables.ready then return 'complete' end return 'wait'",
                        eventSource = "if eventName == 'ready' then return { result = 'complete', output = { by = event.by } } end"
                    })))
                .AddTransition("compute", "decide")
                .Build();
            var engine = CreateScriptingServices().GetRequiredService<IWorkflowEngine>();

            var started = (await engine.StartAsync(definition)).Succeeded();
            Assert.Equal("42", started.Outputs["compute"]!.ToJsonString());
            Assert.Equal(WorkflowTokenStatus.Waiting, started.SingleToken().Status);

            Assert.Equal(WorkflowOperationStatus.NotHandled, (await engine.PublishAsync(started.Id, new WorkflowEvent("other", new JsonObject()))).Status);

            var ready = (await engine.PublishAsync(started.Id, new WorkflowEvent("ready", new JsonObject { ["by"] = "luis" }))).Succeeded();
            Assert.Equal(WorkflowStatus.Completed, ready.Status);
            Assert.Equal("""{"by":"luis"}""", ready.Outputs["decide"]!.ToJsonString());
        }

        [Fact]
        public async Task EachScriptCanChooseItsLanguageAndInvoker()
        {
            var definition = new WorkflowDefinitionBuilder("mixed-languages")
                .AddBranch("main", branch => branch
                    .AddNode("check")
                    .AddNode("big", node => node.OnEnter(pipeline => pipeline.Step("script", new { source = "return 'lua says big'", assignTo = "message" })))
                    .AddNode("small"))
                .AddTransition("check", "big", transition => transition
                    .WithPriority(1)
                    .When("script", new
                    {
                        language = "CSharp",
                        invokerKey = "csharp",
                        source = "(long)Parameters[\"amount\"] > 100",
                        parameters = new { amount = "variables.amount" }
                    }))
                .AddTransition("check", "small")
                .Build();
            var engine = CreateScriptingServices().GetRequiredService<IWorkflowEngine>();

            var instance = (await engine.StartAsync(definition, new() { Variables = new JsonObject { ["amount"] = 500 } })).Succeeded();

            Assert.Equal("lua says big", instance.Variables["message"]!.GetValue<string>());
            Assert.DoesNotContain(instance.History, entry => entry.ElementId == "small");
        }

        [Theory]
        [InlineData("script", """{ "source": "error('no stock')" }""", "no stock")]
        [InlineData("script", """{ "source": "return 42", "assignTo": "x", "language": "Python" }""", "Python")]
        public async Task ScriptFailuresFaultTheInstance(string type, string settings, string expectedMessage)
        {
            var definition = new WorkflowDefinitionBuilder("failing-script")
                .AddBranch("main", branch => branch.AddNode("run", node => node.OnEnter(pipeline => pipeline.Step(type, JsonNode.Parse(settings)))))
                .Build();

            var result = await CreateScriptingServices().GetRequiredService<IWorkflowEngine>().StartAsync(definition);

            Assert.Equal(WorkflowOperationStatus.Faulted, result.Status);
            Assert.Equal("script", result.Instance!.Fault!.ComponentType);
            Assert.Contains(expectedMessage, result.Instance.Fault.Message);
        }

        [Fact]
        public async Task ConditionsMustReturnBooleans()
        {
            var definition = new WorkflowDefinitionBuilder("bad-condition")
                .AddBranch("main", branch => branch.AddNode("a").AddNode("b"))
                .AddTransition("a", "b", transition => transition.When("script", new { source = "return 'yes'" }))
                .Build();

            var result = await CreateScriptingServices().GetRequiredService<IWorkflowEngine>().StartAsync(definition);

            Assert.Equal(WorkflowOperationStatus.Faulted, result.Status);
            Assert.Contains("must return a boolean", result.Instance!.Fault!.Message);
        }

        [Fact]
        public void ScriptComponentsAreRegisteredForEveryKind()
        {
            var catalog = CreateScriptingServices().GetRequiredService<WorkflowComponentCatalog>();

            foreach (var kind in Enum.GetValues<WorkflowComponentKind>())
                Assert.True(catalog.Contains(kind, "script"), $"script is not registered as {kind}");
            Assert.Contains("assignTo", catalog.Get(WorkflowComponentKind.Step, "script").GetSettingsSchema()!.ToJsonString());
            Assert.Contains("eventSource", catalog.Get(WorkflowComponentKind.NodeType, "script").GetSettingsSchema()!.ToJsonString());
        }
    }
}

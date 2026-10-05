using System.Text.Json.Nodes;
using Butterfly.Workflows.Definitions;
using Butterfly.Workflows.Runtime;
using static Butterfly.Workflows.Tests.WorkflowTestHost;

namespace Butterfly.Workflows.Tests
{
    public sealed class BranchingTests
    {
        static WorkflowDefinition ParallelApprovals(JoinMode join)
            => new WorkflowDefinitionBuilder("parallel")
                .AddBranch("main", branch => branch
                    .AddNode("approvals", node => node
                        .RunChildBranches(ChildBranchMode.Parallel, join)
                        .AddBranch("legal", legal => legal.AddNode("legal-review", review => review.OfType("wait", new { @event = "legal-ok" })))
                        .AddBranch("finance", finance => finance.AddNode("finance-review", review => review.OfType("wait", new { @event = "finance-ok" }))))
                    .AddNode("done"))
                .AddTransition("approvals", "done")
                .Build();

        [Fact]
        public async Task ParallelChildBranchesJoinWhenAllFinish()
        {
            var engine = CreateEngine();
            var started = (await engine.StartAsync(ParallelApprovals(JoinMode.All))).Succeeded();

            Assert.Equal(3, started.Tokens.Count);
            var owner = Assert.Single(started.Tokens, token => token.Status == WorkflowTokenStatus.WaitingForChildren);
            Assert.Equal("approvals", owner.NodeId);
            Assert.All(started.Tokens.Where(token => token != owner), child => Assert.Equal(owner.Id, child.ParentId));

            var legal = (await engine.PublishAsync(started.Id, new WorkflowEvent("legal-ok", new JsonObject { ["by"] = "ana" }))).Succeeded();
            Assert.Equal(WorkflowStatus.Running, legal.Status);
            Assert.Equal(2, legal.Tokens.Count);
            Assert.Equal("""{"by":"ana"}""", legal.Outputs["legal-review"]!.ToJsonString());

            var finance = (await engine.PublishAsync(started.Id, new WorkflowEvent("finance-ok"))).Succeeded();
            Assert.Equal(WorkflowStatus.Completed, finance.Status);
            Assert.Contains(finance.History, entry => entry.Kind == WorkflowHistoryKind.NodeEntered && entry.ElementId == "done");
        }

        [Fact]
        public async Task JoinAnyCancelsTheRemainingChildBranches()
        {
            var engine = CreateEngine();
            var started = (await engine.StartAsync(ParallelApprovals(JoinMode.Any))).Succeeded();

            var instance = (await engine.PublishAsync(started.Id, new WorkflowEvent("legal-ok"))).Succeeded();

            Assert.Equal(WorkflowStatus.Completed, instance.Status);
            Assert.Contains(instance.History, entry => entry.Kind == WorkflowHistoryKind.TokenCancelled && entry.ElementId == "finance-review");
        }

        [Fact]
        public async Task EventsCanTargetASingleBranch()
        {
            var definition = new WorkflowDefinitionBuilder("targeted")
                .AddBranch("main", branch => branch.AddNode("fork", node => node
                    .AddBranch("a", a => a.AddNode("a-wait", wait => wait.OfType("wait", new { @event = "done" })))
                    .AddBranch("b", b => b.AddNode("b-wait", wait => wait.OfType("wait", new { @event = "done" })))))
                .Build();
            var engine = CreateEngine();
            var started = (await engine.StartAsync(definition)).Succeeded();

            var instance = (await engine.PublishAsync(started.Id, new WorkflowEvent("done") { TargetId = "b" })).Succeeded();

            Assert.Equal(2, instance.Tokens.Count);
            Assert.Contains(instance.Tokens, token => token.NodeId == "a-wait");
            Assert.DoesNotContain(instance.Tokens, token => token.NodeId == "b-wait");
        }

        [Fact]
        public async Task SequentialChildBranchesRunOneAfterAnother()
        {
            var definition = new WorkflowDefinitionBuilder("sequence")
                .AddBranch("main", branch => branch.AddNode("batch", node => node
                    .RunChildBranches(ChildBranchMode.Sequential)
                    .AddBranch("first", first => first.AddNode("first-step"))
                    .AddBranch("second", second => second.AddNode("second-step"))))
                .Build()
                .Traced();

            var instance = (await CreateEngine().StartAsync(definition)).Succeeded();

            Assert.Equal(
                ["enter:sequence", "enter:main", "enter:batch",
                 "enter:first", "enter:first-step", "exit:first-step", "exit:first",
                 "enter:second", "enter:second-step", "exit:second-step", "exit:second",
                 "exit:batch", "exit:main", "exit:sequence"],
                TraceOf(instance));
        }

        [Fact]
        public async Task TransitionsJumpIntoNestedBranchesOfOtherBranches()
        {
            var definition = new WorkflowDefinitionBuilder("sales")
                .AddBranch("quoting", branch => branch.AddNode("quote", node => node.OfType("wait")))
                .AddBranch("delivery", branch => branch.AddBranch("shipping", shipping => shipping.AddNode("ship", node => node.OfType("wait"))))
                .AddTransition("quote", "ship", transition => transition.OnEvent("accept"))
                .Build()
                .Traced();
            var engine = CreateEngine();
            var started = (await engine.StartAsync(definition)).Succeeded();

            var instance = (await engine.PublishAsync(started.Id, new WorkflowEvent("accept"))).Succeeded();

            Assert.Equal(
                ["enter:sales", "enter:quoting", "enter:quote",
                 "exit:quote", "exit:quoting", "transition:quote->ship", "enter:delivery", "enter:shipping", "enter:ship"],
                TraceOf(instance));
            var token = instance.SingleToken();
            Assert.Equal("ship", token.NodeId);
            Assert.Equal("shipping", token.BranchId);
        }

        [Fact]
        public async Task BranchTransitionsRunWhenTheBranchEndsAndNestedBranchesBubbleUp()
        {
            var definition = new WorkflowDefinitionBuilder("stages")
                .AddBranch("first", branch => branch.AddNode("one"))
                .AddBranch("second", branch => branch
                    .AddNode("two")
                    .AddBranch("sub", sub => sub.AddNode("three")))
                .AddTransition("first", "second")
                .AddTransition("two", "sub")
                .Build()
                .Traced();

            var instance = (await CreateEngine().StartAsync(definition)).Succeeded();

            Assert.Equal(WorkflowStatus.Completed, instance.Status);
            Assert.Equal(
                ["enter:stages", "enter:first", "enter:one", "exit:one", "exit:first", "transition:first->second",
                 "enter:second", "enter:two", "exit:two", "transition:two->sub", "enter:sub", "enter:three",
                 "exit:three", "exit:sub", "exit:second", "exit:stages"],
                TraceOf(instance));
        }

        [Fact]
        public async Task BranchEventsInterruptEverythingInside()
        {
            var definition = new WorkflowDefinitionBuilder("interruptible")
                .AddBranch("main", branch => branch.AddNode("approvals", node => node
                    .AddBranch("a", a => a.AddNode("a-review", review => review.OfType("wait")))
                    .AddBranch("b", b => b.AddNode("b-review", review => review.OfType("wait")))))
                .AddBranch("cleanup", branch => branch.AddNode("aborted"))
                .AddTransition("main", "aborted", transition => transition.OnEvent("abort"))
                .Build();
            var engine = CreateEngine();
            var started = (await engine.StartAsync(definition)).Succeeded();

            var instance = (await engine.PublishAsync(started.Id, new WorkflowEvent("abort"))).Succeeded();

            Assert.Equal(WorkflowStatus.Completed, instance.Status);
            Assert.Equal(2, instance.History.Count(entry => entry.Kind == WorkflowHistoryKind.TokenCancelled));
            Assert.Contains(instance.History, entry => entry.Kind == WorkflowHistoryKind.NodeEntered && entry.ElementId == "aborted");
        }

        [Fact]
        public async Task LeavingAParallelRegionCancelsItsSiblingsAndReplacesTheOwner()
        {
            var definition = new WorkflowDefinitionBuilder("escalation")
                .AddBranch("main", branch => branch
                    .AddNode("review", node => node
                        .AddBranch("legal", legal => legal.AddNode("legal-check", check => check.OfType("wait")))
                        .AddBranch("finance", finance => finance.AddNode("finance-check", check => check.OfType("wait"))))
                    .AddNode("after"))
                .AddBranch("escalated-branch", branch => branch.AddNode("escalated", node => node.OfType("wait")))
                .AddTransition("review", "after")
                .AddTransition("legal-check", "escalated", transition => transition.OnEvent("escalate"))
                .Build();
            var engine = CreateEngine();
            var started = (await engine.StartAsync(definition)).Succeeded();

            var instance = (await engine.PublishAsync(started.Id, new WorkflowEvent("escalate"))).Succeeded();

            var token = instance.SingleToken();
            Assert.Equal("escalated", token.NodeId);
            Assert.Null(token.ParentId);
            Assert.Contains(instance.History, entry => entry.Kind == WorkflowHistoryKind.TokenCancelled && entry.ElementId == "finance-check");
            Assert.Contains(instance.History, entry => entry.Kind == WorkflowHistoryKind.NodeExited && entry.ElementId == "review");
            Assert.DoesNotContain(instance.History, entry => entry.ElementId == "after");
        }

        [Fact]
        public async Task EnteringOneChildBranchFromOutsideMakesItsNodeWaitForIt()
        {
            var definition = new WorkflowDefinitionBuilder("fast-track")
                .AddBranch("main", branch => branch
                    .AddNode("intake", node => node.OfType("wait"))
                    .AddNode("review", node => node
                        .AddBranch("legal", legal => legal.AddNode("legal-check", check => check.OfType("wait", new { @event = "legal-ok" })))
                        .AddBranch("finance", finance => finance.AddNode("finance-check", check => check.OfType("wait"))))
                    .AddNode("closed"))
                .AddTransition("intake", "legal-check", transition => transition.OnEvent("fast-track"))
                .AddTransition("review", "closed")
                .Build();
            var engine = CreateEngine();
            var started = (await engine.StartAsync(definition)).Succeeded();

            var tracked = (await engine.PublishAsync(started.Id, new WorkflowEvent("fast-track"))).Succeeded();

            Assert.Equal(2, tracked.Tokens.Count);
            var owner = Assert.Single(tracked.Tokens, token => token.Status == WorkflowTokenStatus.WaitingForChildren);
            Assert.Equal("review", owner.NodeId);
            Assert.Equal(owner.Id, Assert.Single(tracked.Tokens, token => token.NodeId == "legal-check").ParentId);
            Assert.DoesNotContain(tracked.History, entry => entry.ElementId == "finance-check");

            var closed = (await engine.PublishAsync(started.Id, new WorkflowEvent("legal-ok"))).Succeeded();
            Assert.Equal(WorkflowStatus.Completed, closed.Status);
            Assert.Contains(closed.History, entry => entry.Kind == WorkflowHistoryKind.NodeEntered && entry.ElementId == "closed");
        }

        [Fact]
        public async Task EndNodesCanStopTheWholeWorkflow()
        {
            var definition = new WorkflowDefinitionBuilder("race")
                .AddBranch("main", branch => branch.AddNode("fork", node => node
                    .AddBranch("fast", fast => fast.AddNode("stop", stop => stop.OfType("end", new { scope = "workflow" })))
                    .AddBranch("slow", slow => slow.AddNode("hold", hold => hold.OfType("wait")))))
                .Build();

            var instance = (await CreateEngine().StartAsync(definition)).Succeeded();

            Assert.Equal(WorkflowStatus.Completed, instance.Status);
            Assert.Empty(instance.Tokens);
            Assert.Contains(instance.History, entry => entry.Kind == WorkflowHistoryKind.TokenCancelled && entry.ElementId == "hold");
        }
    }
}

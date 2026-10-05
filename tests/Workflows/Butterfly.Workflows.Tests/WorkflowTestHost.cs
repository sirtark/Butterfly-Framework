using System.Text.Json.Nodes;
using Butterfly.Workflows.Components;
using Butterfly.Workflows.Definitions;
using Butterfly.Workflows.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace Butterfly.Workflows.Tests
{
    internal static class WorkflowTestHost
    {
        public static ServiceProvider CreateServices(Action<WorkflowBuilder>? configure = null, Action<IServiceCollection>? registerServices = null)
        {
            var services = new ServiceCollection();
            registerServices?.Invoke(services);
            services.AddWorkflows(builder =>
            {
                builder.AddComponentsFromAssembly(typeof(WorkflowTestHost).Assembly);
                configure?.Invoke(builder);
            });
            return services.BuildServiceProvider();
        }

        public static IWorkflowEngine CreateEngine(Action<WorkflowBuilder>? configure = null)
            => CreateServices(configure).GetRequiredService<IWorkflowEngine>();

        // Adds a trace step to the enter and exit pipelines of every element and to every transition.
        public static WorkflowDefinition Traced(this WorkflowDefinition definition)
        {
            AddTrace(definition);
            foreach (var transition in definition.Transitions)
                (transition.Pipeline ??= new()).Steps.Add(new() { Type = "trace" });
            return definition;

            void AddTrace(FlowElementDefinition element)
            {
                foreach (var stage in new[] { PipelineStages.Enter, PipelineStages.Exit })
                {
                    if (!element.Pipelines.TryGetValue(stage, out var pipeline))
                        element.Pipelines[stage] = pipeline = new();
                    pipeline.Steps.Add(new() { Type = "trace" });
                }
                foreach (var branch in ((IBranchContainer)element).Branches)
                {
                    AddTrace(branch);
                    foreach (var node in branch.Nodes)
                        AddTrace(node);
                }
            }
        }

        public static List<string> TraceOf(WorkflowInstance instance)
            => instance.Variables["trace"] is JsonArray trace ? [.. trace.Select(entry => entry!.GetValue<string>())] : [];

        public static WorkflowInstance Succeeded(this WorkflowOperationResult result)
        {
            Assert.True(result.Succeeded, $"Expected success but got {result.Status}: {string.Join("; ", result.Errors.Select(error => error.Message))}");
            return result.Instance!;
        }

        public static WorkflowToken SingleToken(this WorkflowInstance instance)
            => Assert.Single(instance.Tokens);
    }

    [WorkflowComponent("trace")]
    public sealed class TraceStep : IWorkflowStep
    {
        public ValueTask ExecuteAsync(WorkflowContext context)
        {
            if (context.Variables["trace"] is not JsonArray trace)
                context.Variables["trace"] = trace = new JsonArray();
            trace.Add($"{context.Stage ?? "transition"}:{context.Element.Id}");
            return ValueTask.CompletedTask;
        }
    }
}

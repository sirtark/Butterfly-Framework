using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Butterfly.Workflows.Scripting
{
    public static class WorkflowBuilderExtensions
    {
        // Registers the "script" node type, step, validator, condition and output. The scripts run on the IScriptInvoker registered in the container.
        public static WorkflowBuilder AddScripting(this WorkflowBuilder builder, Action<WorkflowScriptingOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(builder, nameof(builder));

            var options = new WorkflowScriptingOptions();
            configure?.Invoke(options);
            builder.Services.RemoveAll<WorkflowScriptingOptions>();
            builder.Services.AddSingleton(options);
            builder.Services.TryAddSingleton<WorkflowScriptRunner>();
            return builder.AddComponentsFromAssembly(typeof(WorkflowBuilderExtensions).Assembly);
        }
    }
}

using System.Text.Json.Nodes;
using Butterfly.Workflows.Serialization;

namespace Butterfly.Workflows.Definitions
{
    public abstract class FlowElementBuilder<TSelf> where TSelf : FlowElementBuilder<TSelf>
    {
        private protected FlowElementBuilder(FlowElementDefinition element) => Element = element;

        private protected FlowElementDefinition Element { get; }

        TSelf Self => (TSelf)this;

        public TSelf Named(string name, string? description = null)
        {
            Element.Name = name;
            Element.Description = description ?? Element.Description;
            return Self;
        }

        public TSelf WithMetadata(string key, object? value)
        {
            (Element.Metadata ??= new())[key] = WorkflowJson.ToNode(value);
            return Self;
        }

        public TSelf At(double x, double y, double? width = null, double? height = null)
        {
            Element.Layout = new() { X = x, Y = y, Width = width, Height = height };
            return Self;
        }

        public TSelf OnEnter(Action<PipelineBuilder> configure) => OnStage(PipelineStages.Enter, configure);
        public TSelf OnExit(Action<PipelineBuilder> configure) => OnStage(PipelineStages.Exit, configure);
        public TSelf OnFault(Action<PipelineBuilder> configure) => OnStage(PipelineStages.Fault, configure);

        public TSelf OnStage(string stage, Action<PipelineBuilder> configure)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(stage, nameof(stage));
            ArgumentNullException.ThrowIfNull(configure, nameof(configure));
            if (!Element.Pipelines.TryGetValue(stage, out var pipeline))
                Element.Pipelines[stage] = pipeline = new();
            configure(new PipelineBuilder(pipeline));
            return Self;
        }

        public TSelf WithOutput(string type, object? settings = null)
        {
            Element.Output = new() { Type = type, Settings = WorkflowJson.ToObject(settings) };
            return Self;
        }
    }

    public sealed class WorkflowDefinitionBuilder : FlowElementBuilder<WorkflowDefinitionBuilder>
    {
        readonly WorkflowDefinition _definition;

        public WorkflowDefinitionBuilder(string id) : this(new WorkflowDefinition { Id = id }) { }

        WorkflowDefinitionBuilder(WorkflowDefinition definition) : base(definition) => _definition = definition;

        public WorkflowDefinitionBuilder WithVersion(int version)
        {
            _definition.Version = version;
            return this;
        }

        public WorkflowDefinitionBuilder StartAt(string elementId)
        {
            _definition.StartId = elementId;
            return this;
        }

        public WorkflowDefinitionBuilder WithVariables(object variables)
        {
            _definition.Variables = WorkflowJson.ToObject(variables);
            return this;
        }

        public WorkflowDefinitionBuilder Configure(Action<WorkflowDefinitionOptions> configure)
        {
            configure(_definition.Options);
            return this;
        }

        public WorkflowDefinitionBuilder AddEvent(string id, Action<EventBuilder>? configure = null)
        {
            var workflowEvent = new EventDefinition { Id = id };
            _definition.Events.Add(workflowEvent);
            configure?.Invoke(new EventBuilder(workflowEvent));
            return this;
        }

        public WorkflowDefinitionBuilder AddBranch(string id, Action<BranchBuilder>? configure = null)
        {
            _definition.Branches.Add(BranchBuilder.Create(id, configure));
            return this;
        }

        public WorkflowDefinitionBuilder AddTransition(string sourceId, string targetId, Action<TransitionBuilder>? configure = null)
        {
            var transition = new TransitionDefinition { SourceId = sourceId, TargetId = targetId };
            configure?.Invoke(new TransitionBuilder(transition));
            if (string.IsNullOrEmpty(transition.Id))
                transition.Id = UniqueTransitionId($"{sourceId}->{targetId}");
            _definition.Transitions.Add(transition);
            return this;
        }

        public WorkflowDefinition Build() => _definition;

        string UniqueTransitionId(string baseId)
        {
            var id = baseId;
            for (var suffix = 2; _definition.Transitions.Any(transition => transition.Id == id); suffix++)
                id = $"{baseId}#{suffix}";
            return id;
        }
    }

    public sealed class BranchBuilder : FlowElementBuilder<BranchBuilder>
    {
        readonly BranchDefinition _branch;

        BranchBuilder(BranchDefinition branch) : base(branch) => _branch = branch;

        internal static BranchDefinition Create(string id, Action<BranchBuilder>? configure)
        {
            var branch = new BranchDefinition { Id = id };
            configure?.Invoke(new BranchBuilder(branch));
            return branch;
        }

        public BranchBuilder StartAt(string nodeId)
        {
            _branch.StartNodeId = nodeId;
            return this;
        }

        public BranchBuilder DenyExternalEntry()
        {
            _branch.AllowExternalEntry = false;
            return this;
        }

        public BranchBuilder DenyExternalExit()
        {
            _branch.AllowExternalExit = false;
            return this;
        }

        public BranchBuilder AddNode(string id, Action<NodeBuilder>? configure = null)
        {
            var node = new NodeDefinition { Id = id };
            configure?.Invoke(new NodeBuilder(node));
            _branch.Nodes.Add(node);
            return this;
        }

        public BranchBuilder AddBranch(string id, Action<BranchBuilder>? configure = null)
        {
            _branch.Branches.Add(Create(id, configure));
            return this;
        }
    }

    public sealed class NodeBuilder : FlowElementBuilder<NodeBuilder>
    {
        readonly NodeDefinition _node;

        internal NodeBuilder(NodeDefinition node) : base(node) => _node = node;

        public NodeBuilder OfType(string type, object? settings = null)
        {
            _node.Type = type;
            _node.Settings = WorkflowJson.ToObject(settings) ?? _node.Settings;
            return this;
        }

        public NodeBuilder WithSettings(object settings)
        {
            _node.Settings = WorkflowJson.ToObject(settings);
            return this;
        }

        public NodeBuilder RunChildBranches(ChildBranchMode mode, JoinMode join = JoinMode.All)
        {
            _node.ChildBranchMode = mode;
            _node.JoinMode = join;
            return this;
        }

        public NodeBuilder AddBranch(string id, Action<BranchBuilder>? configure = null)
        {
            _node.Branches.Add(BranchBuilder.Create(id, configure));
            return this;
        }
    }

    public sealed class TransitionBuilder
    {
        readonly TransitionDefinition _transition;

        internal TransitionBuilder(TransitionDefinition transition) => _transition = transition;

        public TransitionBuilder WithId(string id)
        {
            _transition.Id = id;
            return this;
        }

        public TransitionBuilder Named(string name, string? description = null)
        {
            _transition.Name = name;
            _transition.Description = description ?? _transition.Description;
            return this;
        }

        public TransitionBuilder OnEvent(string eventName)
        {
            _transition.Trigger = TransitionTrigger.Event;
            _transition.Event = eventName;
            return this;
        }

        public TransitionBuilder Manual()
        {
            _transition.Trigger = TransitionTrigger.Manual;
            return this;
        }

        public TransitionBuilder WithPriority(int priority)
        {
            _transition.Priority = priority;
            return this;
        }

        public TransitionBuilder When(string type, object? settings = null)
        {
            _transition.Conditions.Add(new() { Type = type, Settings = WorkflowJson.ToObject(settings) });
            return this;
        }

        public TransitionBuilder Unless(string type, object? settings = null)
        {
            _transition.Conditions.Add(new() { Type = type, Settings = WorkflowJson.ToObject(settings), Negate = true });
            return this;
        }

        public TransitionBuilder WithPipeline(Action<PipelineBuilder> configure)
        {
            configure(new PipelineBuilder(_transition.Pipeline ??= new()));
            return this;
        }

        public TransitionBuilder WithMetadata(string key, object? value)
        {
            (_transition.Metadata ??= new())[key] = WorkflowJson.ToNode(value);
            return this;
        }
    }

    public sealed class EventBuilder
    {
        readonly EventDefinition _event;

        internal EventBuilder(EventDefinition workflowEvent) => _event = workflowEvent;

        public EventBuilder Named(string name, string? description = null)
        {
            _event.Name = name;
            _event.Description = description ?? _event.Description;
            return this;
        }

        public EventBuilder StartsWorkflow(bool starts = true)
        {
            _event.StartsWorkflow = starts;
            return this;
        }

        public EventBuilder WithPayloadSchema(JsonObject schema)
        {
            _event.PayloadSchema = schema;
            return this;
        }

        public EventBuilder WithPipeline(Action<PipelineBuilder> configure)
        {
            configure(new PipelineBuilder(_event.Pipeline ??= new()));
            return this;
        }
    }

    public sealed class PipelineBuilder
    {
        readonly PipelineDefinition _pipeline;

        internal PipelineBuilder(PipelineDefinition pipeline) => _pipeline = pipeline;

        public PipelineBuilder Validate(string type, object? settings = null, string? message = null)
        {
            _pipeline.Validations.Add(new() { Type = type, Settings = WorkflowJson.ToObject(settings), Message = message });
            return this;
        }

        public PipelineBuilder Step(string type, object? settings = null, Action<StepBuilder>? configure = null)
        {
            var step = new StepDefinition { Type = type, Settings = WorkflowJson.ToObject(settings) };
            configure?.Invoke(new StepBuilder(step));
            _pipeline.Steps.Add(step);
            return this;
        }
    }

    public sealed class StepBuilder
    {
        readonly StepDefinition _step;

        internal StepBuilder(StepDefinition step) => _step = step;

        public StepBuilder Named(string name)
        {
            _step.Name = name;
            return this;
        }

        public StepBuilder When(string type, object? settings = null)
        {
            _step.Conditions.Add(new() { Type = type, Settings = WorkflowJson.ToObject(settings) });
            return this;
        }

        public StepBuilder Unless(string type, object? settings = null)
        {
            _step.Conditions.Add(new() { Type = type, Settings = WorkflowJson.ToObject(settings), Negate = true });
            return this;
        }

        public StepBuilder ContinueOnError()
        {
            _step.OnError = StepErrorBehavior.Continue;
            return this;
        }
    }
}

using Butterfly.Workflows.Components;

namespace Butterfly.Workflows.Definitions
{
    public sealed record WorkflowDefinitionError(string Code, string Message, string? ElementId = null);

    // Structural checks for definitions coming from code, a database or an editor. Component types are checked when a catalog is given.
    public sealed class WorkflowDefinitionValidator(WorkflowComponentCatalog? catalog = null)
    {
        public IReadOnlyList<WorkflowDefinitionError> Validate(WorkflowDefinition definition)
        {
            ArgumentNullException.ThrowIfNull(definition, nameof(definition));
            return new Validation(definition, catalog).Run();
        }

        public void EnsureValid(WorkflowDefinition definition)
        {
            var errors = Validate(definition);
            if (errors.Count > 0)
                throw new WorkflowDefinitionException(definition.Id, errors);
        }

        sealed class Validation(WorkflowDefinition definition, WorkflowComponentCatalog? catalog)
        {
            readonly List<WorkflowDefinitionError> _errors = [];
            readonly Dictionary<string, FlowElementDefinition> _elements = [];
            readonly Dictionary<string, string?> _parents = [];

            public List<WorkflowDefinitionError> Run()
            {
                if (string.IsNullOrWhiteSpace(definition.Id))
                    Error("workflow.id.required", "The workflow requires an id.");
                if (definition.Branches.Count == 0)
                    Error("workflow.branches.empty", "The workflow needs at least one branch.", definition.Id);

                _elements[WorkflowDefinition.RootId] = definition;
                _parents[WorkflowDefinition.RootId] = null;
                RegisterComponents(definition, definition.Id);
                WalkBranches(definition.Branches, WorkflowDefinition.RootId);

                if (definition.StartId is not null && !(_elements.TryGetValue(definition.StartId, out var start) && start is BranchDefinition or NodeDefinition))
                    Error("workflow.start.notFound", $"The start element '{definition.StartId}' is not a node or branch of the workflow.", definition.Id);

                ValidateEvents();
                ValidateTransitions();
                return _errors;
            }

            void WalkBranches(IEnumerable<BranchDefinition> branches, string parentId)
            {
                foreach (var branch in branches)
                {
                    if (!Register(branch, parentId))
                        continue;
                    if (branch.StartNodeId is not null && branch.Nodes.All(node => node.Id != branch.StartNodeId))
                        Error("branch.start.notFound", $"The branch '{branch.Id}' starts at '{branch.StartNodeId}', which is not one of its nodes.", branch.Id);

                    foreach (var node in branch.Nodes)
                    {
                        if (!Register(node, branch.Id))
                            continue;
                        if (!string.IsNullOrEmpty(node.Type))
                            Component(WorkflowComponentKind.NodeType, node.Type, node.Id);
                        WalkBranches(node.Branches, node.Id);
                    }
                    WalkBranches(branch.Branches, branch.Id);
                }
            }

            bool Register(FlowElementDefinition element, string? parentId)
            {
                if (string.IsNullOrWhiteSpace(element.Id))
                {
                    Error("element.id.required", $"A {element.GetType().Name.Replace("Definition", string.Empty).ToLowerInvariant()} inside '{parentId}' has no id.", parentId);
                    return false;
                }
                if (element.Id.StartsWith('$'))
                {
                    Error("element.id.reserved", $"The id '{element.Id}' is reserved; ids cannot start with '$'.", element.Id);
                    return false;
                }
                if (!_elements.TryAdd(element.Id, element))
                {
                    Error("element.id.duplicate", $"The id '{element.Id}' is used by more than one element.", element.Id);
                    return false;
                }

                _parents[element.Id] = parentId;
                RegisterComponents(element, element.Id);
                return true;
            }

            void RegisterComponents(FlowElementDefinition element, string ownerId)
            {
                foreach (var pipeline in element.Pipelines.Values)
                    Pipeline(pipeline, ownerId);
                if (element.Output is not null)
                    Component(WorkflowComponentKind.Output, element.Output.Type, ownerId);
            }

            void ValidateEvents()
            {
                var ids = new HashSet<string>();
                foreach (var workflowEvent in definition.Events)
                {
                    if (string.IsNullOrWhiteSpace(workflowEvent.Id))
                        Error("event.id.required", "An event has no id.");
                    else if (!ids.Add(workflowEvent.Id))
                        Error("event.id.duplicate", $"The event '{workflowEvent.Id}' is declared more than once.", workflowEvent.Id);
                    if (workflowEvent.Pipeline is not null)
                        Pipeline(workflowEvent.Pipeline, workflowEvent.Id);
                }
            }

            void ValidateTransitions()
            {
                var ids = new HashSet<string>();
                foreach (var transition in definition.Transitions)
                {
                    var id = transition.Id;
                    if (string.IsNullOrWhiteSpace(id))
                        Error("transition.id.required", $"The transition from '{transition.SourceId}' to '{transition.TargetId}' has no id.");
                    else if (!ids.Add(id))
                        Error("transition.id.duplicate", $"The transition id '{id}' is used more than once.", id);

                    var sourceFound = IsFlowTarget(transition.SourceId);
                    var targetFound = IsFlowTarget(transition.TargetId);
                    if (!sourceFound)
                        Error("transition.source.notFound", $"The transition '{id}' starts at '{transition.SourceId}', which is not a node or branch.", id);
                    if (!targetFound)
                        Error("transition.target.notFound", $"The transition '{id}' goes to '{transition.TargetId}', which is not a node or branch.", id);

                    if (transition.Trigger == TransitionTrigger.Event)
                    {
                        if (string.IsNullOrWhiteSpace(transition.Event))
                            Error("transition.event.required", $"The transition '{id}' is triggered by an event but does not name it.", id);
                        else if (definition.Options.StrictEvents && definition.Events.All(workflowEvent => workflowEvent.Id != transition.Event))
                            Error("transition.event.undeclared", $"The transition '{id}' waits for '{transition.Event}', which the workflow does not declare.", id);
                    }

                    foreach (var condition in transition.Conditions)
                        Component(WorkflowComponentKind.Condition, condition.Type, id);
                    if (transition.Pipeline is not null)
                        Pipeline(transition.Pipeline, id);

                    if (sourceFound && targetFound)
                        foreach (var denied in DeniedBoundaries(transition.SourceId, transition.TargetId))
                            Error(denied.Code, $"The transition '{id}' {denied.Message}", id);
                }
            }

            IEnumerable<(string Code, string Message)> DeniedBoundaries(string sourceId, string targetId)
            {
                foreach (var branch in Ancestors(targetId).OfType<BranchDefinition>())
                {
                    if (!branch.AllowExternalEntry && !IsWithin(sourceId, branch.Id))
                        yield return ("transition.entry.denied", $"enters the branch '{branch.Id}', which does not allow external entry.");
                }
                foreach (var branch in Ancestors(sourceId).OfType<BranchDefinition>())
                {
                    if (!branch.AllowExternalExit && !IsWithin(targetId, branch.Id))
                        yield return ("transition.exit.denied", $"leaves the branch '{branch.Id}', which does not allow external exit.");
                }
            }

            IEnumerable<FlowElementDefinition> Ancestors(string id)
            {
                for (string? current = id; current is not null && _elements.TryGetValue(current, out var element); current = _parents.GetValueOrDefault(current))
                    yield return element;
            }

            bool IsWithin(string id, string scopeId)
                => Ancestors(id).Any(element => element.Id == scopeId);

            bool IsFlowTarget(string id)
                => _elements.TryGetValue(id, out var element) && element is BranchDefinition or NodeDefinition;

            void Pipeline(PipelineDefinition pipeline, string ownerId)
            {
                foreach (var validation in pipeline.Validations)
                    Component(WorkflowComponentKind.Validator, validation.Type, ownerId);
                foreach (var step in pipeline.Steps)
                {
                    Component(WorkflowComponentKind.Step, step.Type, ownerId);
                    foreach (var condition in step.Conditions)
                        Component(WorkflowComponentKind.Condition, condition.Type, ownerId);
                }
            }

            void Component(WorkflowComponentKind kind, string type, string? ownerId)
            {
                if (string.IsNullOrWhiteSpace(type))
                    Error("component.type.required", $"A {kind} of '{ownerId}' has no type.", ownerId);
                else if (catalog is not null && !catalog.Contains(kind, type))
                    Error("component.type.unknown", $"'{ownerId}' uses the {kind} '{type}', which is not registered.", ownerId);
            }

            void Error(string code, string message, string? elementId = null)
                => _errors.Add(new(code, message, elementId));
        }
    }
}

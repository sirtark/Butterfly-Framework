namespace Butterfly.Workflows.Definitions
{
    // Lookup structure over a valid definition: the element tree, outgoing transitions and declared events.
    internal sealed class WorkflowDefinitionIndex
    {
        readonly Dictionary<string, Entry> _elements = [];
        readonly Dictionary<string, TransitionDefinition> _transitions = [];
        readonly Dictionary<string, List<TransitionDefinition>> _outgoing = [];
        readonly Dictionary<string, EventDefinition> _events = [];

        readonly record struct Entry(FlowElementDefinition Element, string? ParentId, int Depth);

        public WorkflowDefinitionIndex(WorkflowDefinition definition)
        {
            Definition = definition;
            _elements.Add(WorkflowDefinition.RootId, new(definition, null, 0));
            AddBranches(definition.Branches, WorkflowDefinition.RootId, 1);

            foreach (var transition in definition.Transitions)
                _transitions[transition.Id] = transition;
            foreach (var group in definition.Transitions.GroupBy(transition => transition.SourceId))
                _outgoing[group.Key] = [.. group.OrderByDescending(transition => transition.Priority)];
            foreach (var workflowEvent in definition.Events)
                _events[workflowEvent.Id] = workflowEvent;
        }

        public WorkflowDefinition Definition { get; }

        public FlowElementDefinition Get(string id)
            => _elements.TryGetValue(id, out var entry)
                ? entry.Element
                : throw new WorkflowException($"The workflow '{Definition.Id}' has no node or branch '{id}'.");

        public bool TryGet(string id, out FlowElementDefinition element)
        {
            var found = _elements.TryGetValue(id, out var entry);
            element = entry.Element;
            return found;
        }

        public string? ParentOf(string id) => _elements[id].ParentId;
        public int DepthOf(string id) => _elements[id].Depth;

        public bool IsWithin(string id, string scopeId)
        {
            for (string? current = id; current is not null; current = _elements[current].ParentId)
            {
                if (current == scopeId)
                    return true;
            }
            return false;
        }

        // Ids from the element itself up to, but excluding, the scope.
        public List<string> PathUpTo(string id, string scopeId)
        {
            var path = new List<string>();
            for (var current = id; current != scopeId; current = _elements[current].ParentId ?? throw new WorkflowException($"'{scopeId}' does not contain '{id}'."))
                path.Add(current);
            return path;
        }

        public string CommonAncestor(string left, string right)
        {
            while (DepthOf(left) > DepthOf(right))
                left = ParentOf(left)!;
            while (DepthOf(right) > DepthOf(left))
                right = ParentOf(right)!;
            while (left != right)
            {
                left = ParentOf(left)!;
                right = ParentOf(right)!;
            }
            return left;
        }

        public IEnumerable<string> AncestorsAndSelf(string id)
        {
            for (string? current = id; current is not null; current = _elements[current].ParentId)
                yield return current;
        }

        public IReadOnlyList<TransitionDefinition> Outgoing(string sourceId)
            => _outgoing.TryGetValue(sourceId, out var transitions) ? transitions : [];

        public TransitionDefinition? FindTransition(string id) => _transitions.GetValueOrDefault(id);
        public EventDefinition? FindEvent(string name) => _events.GetValueOrDefault(name);

        public static string KeyOf(WorkflowElementDefinition element)
            => element is WorkflowDefinition ? WorkflowDefinition.RootId : element.Id;

        public static NodeDefinition? StartNodeOf(BranchDefinition branch)
            => branch.StartNodeId is null ? branch.Nodes.FirstOrDefault() : branch.Nodes.FirstOrDefault(node => node.Id == branch.StartNodeId);

        void AddBranches(IEnumerable<BranchDefinition> branches, string parentId, int depth)
        {
            foreach (var branch in branches)
            {
                Add(branch, parentId, depth);
                foreach (var node in branch.Nodes)
                {
                    Add(node, branch.Id, depth + 1);
                    AddBranches(node.Branches, node.Id, depth + 2);
                }
                AddBranches(branch.Branches, branch.Id, depth + 1);
            }
        }

        void Add(FlowElementDefinition element, string? parentId, int depth)
        {
            if (!_elements.TryAdd(element.Id, new(element, parentId, depth)))
                throw new WorkflowException($"The id '{element.Id}' is used by more than one element of the workflow '{Definition.Id}'.");
        }
    }
}

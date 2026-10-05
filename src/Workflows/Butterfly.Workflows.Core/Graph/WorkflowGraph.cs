using System.Text;
using System.Text.Json.Nodes;
using Butterfly.Workflows.Definitions;
using Butterfly.Workflows.Runtime;

namespace Butterfly.Workflows.Graph
{
    public enum WorkflowGraphNodeKind : byte
    {
        Workflow,
        Branch,
        Node
    }

    public enum WorkflowGraphNodeState : byte
    {
        Idle,
        Visited,
        Waiting,
        WaitingForChildren,
        Running
    }

    public sealed class WorkflowGraphNode
    {
        public string Id { get; set; } = string.Empty;
        public string? ParentId { get; set; }
        public WorkflowGraphNodeKind Kind { get; set; }
        public string Label { get; set; } = string.Empty;
        public string? Type { get; set; }
        public bool IsStart { get; set; }
        public ElementLayout? Layout { get; set; }
        public WorkflowGraphNodeState State { get; set; }
        public JsonObject? Metadata { get; set; }
    }

    public sealed class WorkflowGraphEdge
    {
        public string Id { get; set; } = string.Empty;
        public string SourceId { get; set; } = string.Empty;
        public string TargetId { get; set; } = string.Empty;
        public string? Label { get; set; }
        public TransitionTrigger Trigger { get; set; }
        public string? Event { get; set; }
        public bool HasConditions { get; set; }
        public bool Taken { get; set; }
    }

    // A flat, UI-friendly view of a definition: compound nodes through ParentId and transitions as edges, optionally with an instance's state.
    public sealed class WorkflowGraph
    {
        public string WorkflowId { get; set; } = string.Empty;
        public List<WorkflowGraphNode> Nodes { get; set; } = [];
        public List<WorkflowGraphEdge> Edges { get; set; } = [];

        public static WorkflowGraph Create(WorkflowDefinition definition, WorkflowInstance? instance = null)
        {
            ArgumentNullException.ThrowIfNull(definition, nameof(definition));
            var graph = new WorkflowGraph { WorkflowId = definition.Id };
            var startId = definition.StartId ?? definition.Branches.FirstOrDefault()?.Id;

            var visited = instance?.History
                .Where(entry => entry.Kind is WorkflowHistoryKind.NodeEntered or WorkflowHistoryKind.BranchEntered)
                .Select(entry => entry.ElementId)
                .ToHashSet() ?? [];
            var taken = instance?.History
                .Where(entry => entry.Kind == WorkflowHistoryKind.TransitionTaken)
                .Select(entry => entry.ElementId)
                .ToHashSet() ?? [];

            graph.Nodes.Add(new() { Id = WorkflowDefinition.RootId, Kind = WorkflowGraphNodeKind.Workflow, Label = definition.Name ?? definition.Id, Metadata = definition.Metadata });
            AddBranches(definition.Branches, WorkflowDefinition.RootId);

            foreach (var transition in definition.Transitions)
            {
                graph.Edges.Add(new()
                {
                    Id = transition.Id,
                    SourceId = transition.SourceId,
                    TargetId = transition.TargetId,
                    Label = transition.Name ?? transition.Event,
                    Trigger = transition.Trigger,
                    Event = transition.Event,
                    HasConditions = transition.Conditions.Count > 0,
                    Taken = taken.Contains(transition.Id)
                });
            }
            return graph;

            void AddBranches(IEnumerable<BranchDefinition> branches, string parentId)
            {
                foreach (var branch in branches)
                {
                    Add(branch, parentId, WorkflowGraphNodeKind.Branch, type: null);
                    foreach (var node in branch.Nodes)
                    {
                        Add(node, branch.Id, WorkflowGraphNodeKind.Node, node.Type);
                        AddBranches(node.Branches, node.Id);
                    }
                    AddBranches(branch.Branches, branch.Id);
                }
            }

            void Add(FlowElementDefinition element, string parentId, WorkflowGraphNodeKind kind, string? type)
            {
                graph.Nodes.Add(new()
                {
                    Id = element.Id,
                    ParentId = parentId,
                    Kind = kind,
                    Label = element.Name ?? element.Id,
                    Type = type,
                    IsStart = element.Id == startId,
                    Layout = element.Layout,
                    Metadata = element.Metadata,
                    State = StateOf(element.Id)
                });
            }

            WorkflowGraphNodeState StateOf(string elementId)
            {
                if (instance?.Tokens.FirstOrDefault(token => token.NodeId == elementId) is { } token)
                {
                    return token.Status switch
                    {
                        WorkflowTokenStatus.Waiting => WorkflowGraphNodeState.Waiting,
                        WorkflowTokenStatus.WaitingForChildren => WorkflowGraphNodeState.WaitingForChildren,
                        _ => WorkflowGraphNodeState.Running
                    };
                }
                return visited.Contains(elementId) ? WorkflowGraphNodeState.Visited : WorkflowGraphNodeState.Idle;
            }
        }

        public string ToMermaid()
        {
            var ids = new Dictionary<string, string>();
            var builder = new StringBuilder("flowchart TD\n");
            var children = Nodes.Where(node => node.ParentId is not null).ToLookup(node => node.ParentId!);

            foreach (var node in children[WorkflowDefinition.RootId])
                WriteNode(node, 1);

            var start = Nodes.FirstOrDefault(node => node.IsStart);
            if (start is not null)
                builder.Append($"  start__((\" \")) --> {IdOf(start.Id)}\n");

            var linkIndex = start is null ? 0 : 1;
            var takenLinks = new List<int>();
            foreach (var edge in Edges)
            {
                var arrow = edge.Trigger == TransitionTrigger.Manual ? "-.->" : "-->";
                var label = edge.Label is null ? string.Empty : $"|\"{Escape(edge.Label)}\"|";
                builder.Append($"  {IdOf(edge.SourceId)} {arrow}{label} {IdOf(edge.TargetId)}\n");
                if (edge.Taken)
                    takenLinks.Add(linkIndex);
                linkIndex++;
            }

            foreach (var node in Nodes.Where(node => node.Kind != WorkflowGraphNodeKind.Workflow))
            {
                var style = node.State switch
                {
                    WorkflowGraphNodeState.Visited => "fill:#dcfce7,stroke:#15803d",
                    WorkflowGraphNodeState.Waiting => "fill:#fef3c7,stroke:#b45309,stroke-width:3px",
                    WorkflowGraphNodeState.WaitingForChildren => "fill:#e0e7ff,stroke:#4338ca,stroke-width:3px",
                    WorkflowGraphNodeState.Running => "fill:#dbeafe,stroke:#1d4ed8,stroke-width:3px",
                    _ => null
                };
                if (style is not null)
                    builder.Append($"  style {IdOf(node.Id)} {style}\n");
            }
            foreach (var index in takenLinks)
                builder.Append($"  linkStyle {index} stroke:#15803d,stroke-width:2px\n");
            return builder.ToString();

            void WriteNode(WorkflowGraphNode node, int depth)
            {
                var indent = new string(' ', depth * 2);
                var nested = children[node.Id].ToList();
                var label = node.Type is null or "task" ? node.Label : $"{node.Label} · {node.Type}";
                if (nested.Count == 0 && node.Kind == WorkflowGraphNodeKind.Node)
                {
                    builder.Append($"{indent}{IdOf(node.Id)}[\"{Escape(label)}\"]\n");
                    return;
                }
                builder.Append($"{indent}subgraph {IdOf(node.Id)}[\"{Escape(label)}\"]\n");
                foreach (var child in nested)
                    WriteNode(child, depth + 1);
                builder.Append($"{indent}end\n");
            }

            string IdOf(string id)
            {
                if (ids.TryGetValue(id, out var mermaidId))
                    return mermaidId;
                var sanitized = string.Concat(id.Select(character => char.IsAsciiLetterOrDigit(character) ? character : '_'));
                mermaidId = $"n_{sanitized}";
                for (var suffix = 2; ids.ContainsValue(mermaidId); suffix++)
                    mermaidId = $"n_{sanitized}_{suffix}";
                return ids[id] = mermaidId;
            }

            static string Escape(string text) => text.Replace("\"", "#quot;");
        }
    }
}

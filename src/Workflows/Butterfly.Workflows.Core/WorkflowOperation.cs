using System.Text.Json.Nodes;
using Butterfly.Workflows.Components;
using Butterfly.Workflows.Components.BuiltIn;
using Butterfly.Workflows.Definitions;
using Butterfly.Workflows.Runtime;

namespace Butterfly.Workflows
{
    // One atomic run over an instance: start, event, manual transition, jump or cancel.
    // Validations anywhere reject the whole run before it is saved; component exceptions fault the instance.
    internal sealed class WorkflowOperation(
        WorkflowDefinitionIndex index,
        WorkflowInstance instance,
        WorkflowEvent? workflowEvent,
        IServiceProvider services,
        WorkflowComponentCatalog catalog,
        WorkflowEngineOptions options,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        enum WorkKind : byte { Activate, Execute, Leave, EndFlow, ChildCompleted }

        readonly record struct WorkItem(WorkKind Kind, string TokenId, NodeResultKind Result = NodeResultKind.Complete);

        readonly Queue<WorkItem> _agenda = new();
        int _activations;

        public WorkflowDefinitionIndex Index { get; } = index;
        public WorkflowInstance Instance { get; } = instance;
        public WorkflowEvent? Event { get; private set; } = workflowEvent;
        public IServiceProvider Services { get; } = services;
        public CancellationToken CancellationToken { get; } = cancellationToken;

        WorkflowDefinition Definition => Index.Definition;

        public async ValueTask<WorkflowOperationStatus> StartAsync()
        {
            var targetId = Definition.StartId ?? Definition.Branches[0].Id;
            var enterPath = EnterPath(WorkflowDefinition.RootId, targetId);
            await ValidateAsync([(Definition, Definition.GetPipeline(PipelineStages.Enter), PipelineStages.Enter), .. EnterChecks(enterPath)], token: null);

            Record(WorkflowHistoryKind.WorkflowStarted, WorkflowDefinition.RootId);
            await RunStepsAsync(Definition, Definition.GetPipeline(PipelineStages.Enter), token: null, PipelineStages.Enter);

            var token = NewToken(parentId: null, WorkflowDefinition.RootId);
            await EnterAsync(token, enterPath);
            await DrainAsync();
            return WorkflowOperationStatus.Succeeded;
        }

        public async ValueTask<WorkflowOperationStatus> PublishAsync()
        {
            var received = Event!;
            Record(WorkflowHistoryKind.EventReceived, message: received.Name);

            var declared = Index.FindEvent(received.Name);
            if (declared is null && Definition.Options.StrictEvents)
                throw new WorkflowRejectedException($"The workflow '{Definition.Id}' does not accept the event '{received.Name}'.");
            if (declared?.Pipeline is { } eventPipeline)
            {
                await ValidateAsync([(declared, eventPipeline, null)], token: null);
                await RunStepsAsync(declared, eventPipeline, token: null, stage: null);
            }

            var resting = Instance.Tokens
                .Where(token => token.Status is WorkflowTokenStatus.Waiting or WorkflowTokenStatus.WaitingForChildren)
                .Where(token => received.TargetId is null || token.Id == received.TargetId || Index.IsWithin(token.PositionId, received.TargetId))
                .ToList();

            if (await FindEventTransitionAsync(resting, received.Name) is { } match)
            {
                await TraverseAsync(CarrierWithin(match.Transition.SourceId)!, match.Transition.SourceId, match.Transition.TargetId, match.Transition, interrupting: true);
                await DrainAsync();
                return WorkflowOperationStatus.Succeeded;
            }

            foreach (var token in resting.Where(token => token.Status == WorkflowTokenStatus.Waiting))
            {
                var node = (NodeDefinition)Index.Get(token.NodeId!);
                var context = new WorkflowContext(this, node, token, stage: null, component: null);
                var result = await InvokeAsync(node, null, NodeTypeOf(node), () => CreateNodeType(node).HandleEventAsync(context));
                if (result.Kind == NodeResultKind.Ignore)
                    continue;
                if (result.Kind != NodeResultKind.Wait)
                    Enqueue(WorkKind.Leave, token, result.Kind);
                await DrainAsync();
                return WorkflowOperationStatus.Succeeded;
            }
            return WorkflowOperationStatus.NotHandled;
        }

        public async ValueTask<WorkflowOperationStatus> TriggerTransitionAsync(string transitionId, JsonNode? payload)
        {
            var transition = Index.FindTransition(transitionId)
                ?? throw new WorkflowRejectedException($"The workflow '{Definition.Id}' has no transition '{transitionId}'.");
            if (transition.Trigger != TransitionTrigger.Manual)
                throw new WorkflowRejectedException($"The transition '{transitionId}' is {transition.Trigger}, not manual.");

            Event = new WorkflowEvent(transition.Event ?? transition.Id, payload);
            var carrier = CarrierWithin(transition.SourceId)
                ?? throw new WorkflowRejectedException($"No token is waiting inside '{transition.SourceId}'.");
            if (!await ConditionsPassAsync(transition.Conditions, transition, carrier, stage: null))
                throw new WorkflowRejectedException($"The conditions of the transition '{transitionId}' are not met.");

            await TraverseAsync(carrier, transition.SourceId, transition.TargetId, transition, interrupting: true);
            await DrainAsync();
            return WorkflowOperationStatus.Succeeded;
        }

        public async ValueTask<WorkflowOperationStatus> JumpAsync(string targetId, string? tokenId, JsonNode? payload)
        {
            if (!Definition.Options.AllowJumps)
                throw new WorkflowRejectedException($"The workflow '{Definition.Id}' does not allow jumps.");
            if (!Index.TryGet(targetId, out var target) || target is WorkflowDefinition)
                throw new WorkflowRejectedException($"'{targetId}' is not a node or branch of the workflow '{Definition.Id}'.");

            var carrier = tokenId is not null
                ? FindToken(tokenId) ?? throw new WorkflowRejectedException($"The instance has no token '{tokenId}'.")
                : SingleLeafToken();
            var sourceId = carrier.PositionId;

            foreach (var branch in Index.AncestorsAndSelf(targetId).Select(Index.Get).OfType<BranchDefinition>())
            {
                if (!branch.AllowExternalEntry && !Index.IsWithin(sourceId, branch.Id))
                    throw new WorkflowRejectedException($"The branch '{branch.Id}' does not allow external entry.");
            }
            foreach (var branch in Index.AncestorsAndSelf(sourceId).Select(Index.Get).OfType<BranchDefinition>())
            {
                if (!branch.AllowExternalExit && !Index.IsWithin(targetId, branch.Id))
                    throw new WorkflowRejectedException($"The branch '{branch.Id}' does not allow external exit.");
            }

            Event = new WorkflowEvent("jump", payload);
            Record(WorkflowHistoryKind.Jumped, targetId, carrier, $"from {sourceId}");
            await TraverseAsync(carrier, sourceId, targetId, transition: null, interrupting: true);
            await DrainAsync();
            return WorkflowOperationStatus.Succeeded;
        }

        public ValueTask<WorkflowOperationStatus> CancelAsync(string? reason)
        {
            foreach (var token in Instance.Tokens)
                Record(WorkflowHistoryKind.TokenCancelled, token.PositionId, token);
            Instance.Tokens.Clear();
            Instance.Status = WorkflowStatus.Cancelled;
            Instance.CompletedAt = time.GetUtcNow();
            Record(WorkflowHistoryKind.WorkflowCancelled, WorkflowDefinition.RootId, message: reason);
            return ValueTask.FromResult(WorkflowOperationStatus.Succeeded);
        }

        public async ValueTask FaultAsync(Exception exception)
        {
            var failure = exception as WorkflowComponentException;
            var cause = failure?.InnerException ?? exception;
            Instance.Status = WorkflowStatus.Faulted;
            Instance.Fault = new()
            {
                ElementId = failure?.ElementId,
                Stage = failure?.Stage,
                ComponentType = failure?.ComponentType,
                Message = cause.Message,
                ExceptionType = cause.GetType().FullName,
                StackTrace = cause.StackTrace,
                Timestamp = time.GetUtcNow()
            };
            Record(WorkflowHistoryKind.WorkflowFaulted, failure?.ElementId, message: cause.Message);

            List<FlowElementDefinition> handlers = [];
            if (failure?.Element is FlowElementDefinition element and not WorkflowDefinition)
                handlers.Add(element);
            handlers.Add(Definition);
            foreach (var handler in handlers)
            {
                try
                {
                    await RunStepsAsync(handler, handler.GetPipeline(PipelineStages.Fault), token: null, PipelineStages.Fault);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Record(WorkflowHistoryKind.StepFailed, WorkflowDefinitionIndex.KeyOf(handler), message: ex.InnerException?.Message ?? ex.Message);
                }
            }
        }

        public void SetOutput(WorkflowElementDefinition element, JsonNode? value)
        {
            var copy = value?.DeepClone();
            if (element is WorkflowDefinition)
                Instance.Output = copy;
            else
                Instance.Outputs[element.Id] = copy;
        }

        async ValueTask DrainAsync()
        {
            while (_agenda.TryDequeue(out var item))
            {
                CancellationToken.ThrowIfCancellationRequested();
                if (Instance.Status != WorkflowStatus.Running || FindToken(item.TokenId) is not { } token)
                    continue;

                switch (item.Kind)
                {
                    case WorkKind.Activate:
                        await ActivateAsync(token);
                        break;
                    case WorkKind.Execute:
                        await ExecuteNodeAsync(token);
                        break;
                    case WorkKind.Leave:
                        await LeaveNodeAsync(token, item.Result);
                        break;
                    case WorkKind.EndFlow:
                        await EndFlowAsync(token);
                        break;
                    case WorkKind.ChildCompleted:
                        await ChildCompletedAsync(token);
                        break;
                }
            }

            if (Instance.Status == WorkflowStatus.Running && Instance.Tokens.Count == 0)
                await CompleteWorkflowAsync();
        }

        // The node was reached: start its child branches, or run it straight away.
        async ValueTask ActivateAsync(WorkflowToken token)
        {
            var node = (NodeDefinition)Index.Get(token.NodeId!);
            CountActivation(node.Id);

            if (node.Branches.Count == 0)
            {
                Enqueue(WorkKind.Execute, token);
                return;
            }

            if (node.JoinMode == JoinMode.None)
            {
                foreach (var branch in node.Branches)
                    await SpawnAsync(token.ParentId, node, branch);
                Enqueue(WorkKind.Execute, token);
                return;
            }

            token.Status = WorkflowTokenStatus.WaitingForChildren;
            if (node.ChildBranchMode == ChildBranchMode.Sequential)
            {
                token.PendingBranchIds = [.. node.Branches.Skip(1).Select(branch => branch.Id)];
                await SpawnAsync(token.Id, node, node.Branches[0]);
            }
            else
            {
                foreach (var branch in node.Branches)
                    await SpawnAsync(token.Id, node, branch);
            }
        }

        async ValueTask ExecuteNodeAsync(WorkflowToken token)
        {
            token.Status = WorkflowTokenStatus.Running;
            var node = (NodeDefinition)Index.Get(token.NodeId!);
            var context = new WorkflowContext(this, node, token, stage: null, component: null);
            var result = await InvokeAsync(node, null, NodeTypeOf(node), () => CreateNodeType(node).ExecuteAsync(context));

            switch (result.Kind)
            {
                case NodeResultKind.Wait:
                    token.Status = WorkflowTokenStatus.Waiting;
                    Record(WorkflowHistoryKind.NodeWaiting, node.Id, token);
                    break;
                case NodeResultKind.Ignore:
                    throw new WorkflowComponentException(node, null, NodeTypeOf(node), new WorkflowException("A node type cannot return Ignore from ExecuteAsync."));
                default:
                    Enqueue(WorkKind.Leave, token, result.Kind);
                    break;
            }
        }

        async ValueTask LeaveNodeAsync(WorkflowToken token, NodeResultKind result)
        {
            var node = Index.Get(token.NodeId!);
            token.Status = WorkflowTokenStatus.Running;

            if (result == NodeResultKind.EndWorkflow)
            {
                await ValidateAsync([(node, node.GetPipeline(PipelineStages.Exit), PipelineStages.Exit)], token);
                await ExitElementAsync(node, token);
                foreach (var other in Instance.Tokens.ToList())
                {
                    if (other != token)
                        Record(WorkflowHistoryKind.TokenCancelled, other.PositionId, other);
                }
                Instance.Tokens.Clear();
                return;
            }

            if (result == NodeResultKind.Complete && await SelectAutomaticTransitionAsync(node.Id, token) is { } transition)
            {
                await TraverseAsync(token, node.Id, transition.TargetId, transition, interrupting: false);
                return;
            }
            await EndFlowAsync(token);
        }

        // The flow ended without a transition: leave the node, then each enclosing branch, until a branch transition,
        // an owning node or the workflow itself takes over.
        async ValueTask EndFlowAsync(WorkflowToken token)
        {
            string scopeId;
            if (token.NodeId is { } nodeId)
            {
                await ValidateAsync([(Index.Get(nodeId), Index.Get(nodeId).GetPipeline(PipelineStages.Exit), PipelineStages.Exit)], token);
                await ExitElementAsync(Index.Get(nodeId), token);
                scopeId = Index.ParentOf(nodeId)!;
            }
            else
            {
                scopeId = token.BranchId;
            }

            while (true)
            {
                var scope = Index.Get(scopeId);
                switch (scope)
                {
                    case WorkflowDefinition:
                        Instance.Tokens.Remove(token);
                        return;

                    case NodeDefinition owner:
                        Instance.Tokens.Remove(token);
                        if (token.ParentId is not null && FindToken(token.ParentId) is { Status: WorkflowTokenStatus.WaitingForChildren } parent && parent.NodeId == owner.Id)
                            Enqueue(WorkKind.ChildCompleted, parent);
                        return;

                    case BranchDefinition branch:
                        token.NodeId = null;
                        token.BranchId = branch.Id;
                        if (await SelectAutomaticTransitionAsync(branch.Id, token) is { } transition)
                        {
                            await TraverseAsync(token, branch.Id, transition.TargetId, transition, interrupting: false);
                            return;
                        }
                        await ValidateAsync([(branch, branch.GetPipeline(PipelineStages.Exit), PipelineStages.Exit)], token);
                        await ExitElementAsync(branch, token);
                        scopeId = Index.ParentOf(branch.Id)!;
                        break;
                }
            }
        }

        async ValueTask ChildCompletedAsync(WorkflowToken owner)
        {
            if (owner.Status != WorkflowTokenStatus.WaitingForChildren)
                return;

            var node = (NodeDefinition)Index.Get(owner.NodeId!);
            var children = ChildrenOf(owner).ToList();

            if (node.JoinMode == JoinMode.Any)
            {
                foreach (var child in children)
                    CancelTree(child);
                owner.PendingBranchIds.Clear();
            }
            else if (children.Count > 0)
            {
                return;
            }
            else if (owner.PendingBranchIds.Count > 0)
            {
                var nextId = owner.PendingBranchIds[0];
                owner.PendingBranchIds.RemoveAt(0);
                await SpawnAsync(owner.Id, node, node.Branches.First(branch => branch.Id == nextId));
                return;
            }

            owner.Status = WorkflowTokenStatus.Running;
            Enqueue(WorkKind.Execute, owner);
        }

        // Moves a token along a transition, jump or interruption: exits up to the common ancestor of both ends and enters down to the target.
        async ValueTask TraverseAsync(WorkflowToken token, string sourceId, string targetId, TransitionDefinition? transition, bool interrupting)
        {
            CountActivation(sourceId);
            var scopeId = Index.CommonAncestor(Index.ParentOf(sourceId)!, Index.ParentOf(targetId)!);
            var exits = Index.PathUpTo(token.PositionId, scopeId).Select(Index.Get).ToList();
            var enters = EnterPath(scopeId, targetId);

            await ValidateAsync(
            [
                .. exits.Select(element => ((WorkflowElementDefinition)element, element.GetPipeline(PipelineStages.Exit), (string?)PipelineStages.Exit)),
                .. transition is null ? [] : new[] { ((WorkflowElementDefinition)transition, transition.Pipeline, (string?)null) },
                .. EnterChecks(enters)
            ], token);

            if (interrupting)
            {
                foreach (var other in Instance.Tokens.ToList())
                {
                    if (other != token && !IsAncestorOf(other, token) && Index.IsWithin(other.PositionId, sourceId) && Instance.Tokens.Contains(other))
                        CancelTree(other);
                }
            }

            foreach (var element in exits)
                await ExitElementAsync(element, token);

            if (transition is not null)
            {
                Record(WorkflowHistoryKind.TransitionTaken, transition.Id, token);
                await RunStepsAsync(transition, transition.Pipeline, token, stage: null);
            }

            token.Status = WorkflowTokenStatus.Running;
            await EnterAsync(token, enters);
        }

        async ValueTask SpawnAsync(string? parentId, NodeDefinition node, BranchDefinition branch)
        {
            var enters = EnterPath(node.Id, branch.Id);
            var child = NewToken(parentId, branch.Id);
            await ValidateAsync(EnterChecks(enters), child);
            await EnterAsync(child, enters);
        }

        async ValueTask EnterAsync(WorkflowToken token, List<FlowElementDefinition> path)
        {
            for (var i = 0; i < path.Count; i++)
            {
                var last = i == path.Count - 1;
                switch (path[i])
                {
                    case BranchDefinition branch:
                        token.BranchId = branch.Id;
                        token.NodeId = null;
                        Record(WorkflowHistoryKind.BranchEntered, branch.Id, token);
                        await RunStepsAsync(branch, branch.GetPipeline(PipelineStages.Enter), token, PipelineStages.Enter);
                        if (last)
                            Enqueue(WorkKind.EndFlow, token);
                        break;

                    case NodeDefinition node:
                        token.NodeId = node.Id;
                        token.BranchId = Index.ParentOf(node.Id)!;
                        Record(WorkflowHistoryKind.NodeEntered, node.Id, token);
                        await RunStepsAsync(node, node.GetPipeline(PipelineStages.Enter), token, PipelineStages.Enter);
                        if (last)
                        {
                            Enqueue(WorkKind.Activate, token);
                        }
                        else
                        {
                            // Entering one of the node's child branches directly: the node waits for this token as its only child.
                            var owner = NewToken(token.ParentId, token.BranchId);
                            owner.NodeId = node.Id;
                            owner.Status = WorkflowTokenStatus.WaitingForChildren;
                            token.ParentId = owner.Id;
                        }
                        break;
                }
            }
        }

        async ValueTask ExitElementAsync(FlowElementDefinition element, WorkflowToken token)
        {
            if (element is NodeDefinition node)
            {
                if (token.NodeId == node.Id)
                {
                    foreach (var child in ChildrenOf(token).ToList())
                        CancelTree(child);
                    token.PendingBranchIds.Clear();
                }
                else if (AncestorAt(token, node.Id) is { } owner)
                {
                    ReleaseOwner(owner, token);
                }
                Record(WorkflowHistoryKind.NodeExited, node.Id, token);
            }
            else
            {
                Record(WorkflowHistoryKind.BranchExited, element.Id, token);
            }

            await RunStepsAsync(element, element.GetPipeline(PipelineStages.Exit), token, PipelineStages.Exit);
            await MapOutputAsync(element, token);
        }

        async ValueTask CompleteWorkflowAsync()
        {
            await ValidateAsync([(Definition, Definition.GetPipeline(PipelineStages.Exit), PipelineStages.Exit)], token: null);
            await RunStepsAsync(Definition, Definition.GetPipeline(PipelineStages.Exit), token: null, PipelineStages.Exit);
            await MapOutputAsync(Definition, token: null);
            Instance.Status = WorkflowStatus.Completed;
            Instance.CompletedAt = time.GetUtcNow();
            Record(WorkflowHistoryKind.WorkflowCompleted, WorkflowDefinition.RootId);
        }

        async ValueTask<(TransitionDefinition Transition, WorkflowToken Token)?> FindEventTransitionAsync(List<WorkflowToken> tokens, string eventName)
        {
            (TransitionDefinition Transition, WorkflowToken Token, int Depth)? best = null;
            foreach (var token in tokens)
            {
                foreach (var elementId in Index.AncestorsAndSelf(token.PositionId))
                {
                    var depth = Index.DepthOf(elementId);
                    foreach (var transition in Index.Outgoing(elementId))
                    {
                        if (transition.Trigger != TransitionTrigger.Event || transition.Event != eventName)
                            continue;
                        var better = best is null || depth > best.Value.Depth || (depth == best.Value.Depth && transition.Priority > best.Value.Transition.Priority);
                        if (better && await ConditionsPassAsync(transition.Conditions, transition, token, stage: null))
                            best = (transition, token, depth);
                    }
                }
            }
            return best is null ? null : (best.Value.Transition, best.Value.Token);
        }

        async ValueTask<TransitionDefinition?> SelectAutomaticTransitionAsync(string sourceId, WorkflowToken token)
        {
            foreach (var transition in Index.Outgoing(sourceId))
            {
                if (transition.Trigger == TransitionTrigger.Automatic && await ConditionsPassAsync(transition.Conditions, transition, token, stage: null))
                    return transition;
            }
            return null;
        }

        async ValueTask ValidateAsync(IEnumerable<(WorkflowElementDefinition Element, PipelineDefinition? Pipeline, string? Stage)> checks, WorkflowToken? token)
        {
            var errors = new List<WorkflowError>();
            foreach (var (element, pipeline, stage) in checks)
            {
                if (pipeline is null)
                    continue;
                foreach (var validation in pipeline.Validations)
                {
                    var context = new WorkflowContext(this, element, token, stage, validation);
                    var messages = await InvokeAsync(element, stage, validation.Type,
                        () => Create<IWorkflowValidator>(WorkflowComponentKind.Validator, validation.Type).ValidateAsync(context));
                    if (messages.Count == 0)
                        continue;
                    if (validation.Message is not null)
                        errors.Add(new(validation.Message, WorkflowDefinitionIndex.KeyOf(element), stage, validation.Type));
                    else
                        errors.AddRange(messages.Select(message => new WorkflowError(message, WorkflowDefinitionIndex.KeyOf(element), stage, validation.Type)));
                }
            }
            if (errors.Count > 0)
                throw new WorkflowRejectedException(errors);
        }

        async ValueTask RunStepsAsync(WorkflowElementDefinition element, PipelineDefinition? pipeline, WorkflowToken? token, string? stage)
        {
            if (pipeline is null)
                return;
            foreach (var step in pipeline.Steps)
            {
                if (!await ConditionsPassAsync(step.Conditions, element, token, stage))
                    continue;
                var context = new WorkflowContext(this, element, token, stage, step);
                try
                {
                    await Create<IWorkflowStep>(WorkflowComponentKind.Step, step.Type).ExecuteAsync(context);
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not WorkflowRejectedException)
                {
                    if (step.OnError != StepErrorBehavior.Continue)
                        throw new WorkflowComponentException(element, stage, step.Type, ex);
                    Record(WorkflowHistoryKind.StepFailed, WorkflowDefinitionIndex.KeyOf(element), token, $"{step.Name ?? step.Type}: {ex.Message}");
                }
            }
        }

        async ValueTask<bool> ConditionsPassAsync(IReadOnlyList<ConditionDefinition> conditions, WorkflowElementDefinition element, WorkflowToken? token, string? stage)
        {
            foreach (var condition in conditions)
            {
                var context = new WorkflowContext(this, element, token, stage, condition);
                var result = await InvokeAsync(element, stage, condition.Type,
                    () => Create<IWorkflowCondition>(WorkflowComponentKind.Condition, condition.Type).EvaluateAsync(context));
                if (result == condition.Negate)
                    return false;
            }
            return true;
        }

        async ValueTask MapOutputAsync(FlowElementDefinition element, WorkflowToken? token)
        {
            if (element.Output is not { } output)
                return;
            var context = new WorkflowContext(this, element, token, stage: null, output);
            var value = await InvokeAsync(element, null, output.Type,
                () => Create<IWorkflowOutput>(WorkflowComponentKind.Output, output.Type).MapAsync(context));
            SetOutput(element, value);
        }

        static async ValueTask<T> InvokeAsync<T>(WorkflowElementDefinition element, string? stage, string componentType, Func<ValueTask<T>> invoke)
        {
            try
            {
                return await invoke();
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not WorkflowRejectedException and not WorkflowComponentException)
            {
                throw new WorkflowComponentException(element, stage, componentType, ex);
            }
        }

        T Create<T>(WorkflowComponentKind kind, string type)
            => (T)catalog.Get(kind, type).Create(Services);

        IWorkflowNodeType CreateNodeType(NodeDefinition node)
            => Create<IWorkflowNodeType>(WorkflowComponentKind.NodeType, NodeTypeOf(node));

        static string NodeTypeOf(NodeDefinition node)
            => string.IsNullOrEmpty(node.Type) ? WorkflowNodeTypes.Task : node.Type;

        List<FlowElementDefinition> EnterPath(string scopeId, string targetId)
        {
            var path = Index.PathUpTo(targetId, scopeId).Select(Index.Get).Reverse().ToList();
            if (path.LastOrDefault() is BranchDefinition branch && WorkflowDefinitionIndex.StartNodeOf(branch) is { } start)
                path.Add(start);
            return path;
        }

        static IEnumerable<(WorkflowElementDefinition, PipelineDefinition?, string?)> EnterChecks(IEnumerable<FlowElementDefinition> path)
            => path.Select(element => ((WorkflowElementDefinition)element, element.GetPipeline(PipelineStages.Enter), (string?)PipelineStages.Enter));

        WorkflowToken NewToken(string? parentId, string branchId)
        {
            var token = new WorkflowToken
            {
                Id = Guid.CreateVersion7().ToString("N"),
                ParentId = parentId,
                BranchId = branchId,
                Status = WorkflowTokenStatus.Running,
                CreatedAt = time.GetUtcNow()
            };
            Instance.Tokens.Add(token);
            return token;
        }

        // The token that carries an interrupting transition: the outermost one inside the source.
        WorkflowToken? CarrierWithin(string sourceId)
            => Instance.Tokens
                .Where(token => token.Status is WorkflowTokenStatus.Waiting or WorkflowTokenStatus.WaitingForChildren && Index.IsWithin(token.PositionId, sourceId))
                .OrderBy(token => Index.DepthOf(token.PositionId))
                .FirstOrDefault();

        WorkflowToken SingleLeafToken()
        {
            var leaves = Instance.Tokens.Where(token => !ChildrenOf(token).Any()).ToList();
            return leaves.Count == 1
                ? leaves[0]
                : throw new WorkflowRejectedException($"The instance has {leaves.Count} active tokens; say which one should jump.");
        }

        WorkflowToken? FindToken(string id) => Instance.Tokens.FirstOrDefault(token => token.Id == id);

        IEnumerable<WorkflowToken> ChildrenOf(WorkflowToken token) => Instance.Tokens.Where(child => child.ParentId == token.Id);

        bool IsAncestorOf(WorkflowToken candidate, WorkflowToken token)
        {
            for (var current = token.ParentId is null ? null : FindToken(token.ParentId); current is not null; current = current.ParentId is null ? null : FindToken(current.ParentId))
            {
                if (current == candidate)
                    return true;
            }
            return false;
        }

        WorkflowToken? AncestorAt(WorkflowToken token, string nodeId)
        {
            for (var current = token.ParentId is null ? null : FindToken(token.ParentId); current is not null; current = current.ParentId is null ? null : FindToken(current.ParentId))
            {
                if (current.NodeId == nodeId)
                    return current;
            }
            return null;
        }

        // The token leaves a node that was waiting for it: the node's other children are cancelled and the token takes the node's place.
        void ReleaseOwner(WorkflowToken owner, WorkflowToken token)
        {
            var chain = token;
            while (chain.ParentId != owner.Id)
                chain = FindToken(chain.ParentId!)!;
            foreach (var child in ChildrenOf(owner).Where(child => child != chain).ToList())
                CancelTree(child);
            chain.ParentId = owner.ParentId;
            Instance.Tokens.Remove(owner);
        }

        void CancelTree(WorkflowToken token)
        {
            foreach (var child in ChildrenOf(token).ToList())
                CancelTree(child);
            Instance.Tokens.Remove(token);
            Record(WorkflowHistoryKind.TokenCancelled, token.PositionId, token);
        }

        void Enqueue(WorkKind kind, WorkflowToken token, NodeResultKind result = NodeResultKind.Complete)
            => _agenda.Enqueue(new(kind, token.Id, result));

        void CountActivation(string elementId)
        {
            if (++_activations > options.MaxActivationsPerOperation)
                throw new WorkflowComponentException(Index.TryGet(elementId, out var element) ? element : null, null, null,
                    new WorkflowException($"The operation exceeded {options.MaxActivationsPerOperation} activations; the workflow probably loops without waiting for an event."));
        }

        void Record(WorkflowHistoryKind kind, string? elementId = null, WorkflowToken? token = null, string? message = null)
        {
            if (options.RecordHistory)
                Instance.History.Add(new() { Timestamp = time.GetUtcNow(), Kind = kind, ElementId = elementId, TokenId = token?.Id, Message = message });
        }
    }
}

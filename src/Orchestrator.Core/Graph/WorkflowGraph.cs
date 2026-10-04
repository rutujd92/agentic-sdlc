namespace Orchestrator.Core.Graph;

/// <summary>
/// Immutable, validated dependency graph. Validation collects every problem at once (duplicate ids,
/// unknown dependencies, cycles with their path, entry count) so a planner gets complete feedback.
/// Ordering is deterministic: ties are broken by declaration order.
/// </summary>
public sealed class WorkflowGraph
{
    private readonly Dictionary<string, WorkflowNode> _byId;

    private WorkflowGraph(IReadOnlyList<WorkflowNode> nodes, IReadOnlyList<IReadOnlyList<WorkflowNode>> layers)
    {
        Nodes = nodes;
        Layers = layers;
        TopologicalOrder = layers.SelectMany(l => l).ToArray();
        _byId = nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
    }

    public IReadOnlyList<WorkflowNode> Nodes { get; }

    /// <summary>Nodes grouped by depth; nodes in the same layer have no path between them and may run in parallel.</summary>
    public IReadOnlyList<IReadOnlyList<WorkflowNode>> Layers { get; }

    public IReadOnlyList<WorkflowNode> TopologicalOrder { get; }

    public WorkflowNode Entry => Layers[0][0];

    public static WorkflowGraph Create(IEnumerable<WorkflowNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var list = nodes.ToArray();
        var errors = Validate(list);
        if (errors.Count > 0)
        {
            throw new InvalidWorkflowGraphException(errors);
        }

        return new WorkflowGraph(list, BuildLayers(list));
    }

    /// <summary>Returns a new validated graph with the change applied; throws <see cref="InvalidWorkflowGraphException"/> if invalid.</summary>
    public WorkflowGraph Apply(GraphChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var unknown = change.AddedDependencies.Where(e => !Contains(e.Node)).Select(e => $"Edge targets unknown node '{e.Node}'.").ToArray();
        if (unknown.Length > 0)
        {
            throw new InvalidWorkflowGraphException(unknown);
        }

        var rewired = Nodes.Select(n =>
        {
            var extra = change.AddedDependencies.Where(e => e.Node == n.Id).Select(e => e.DependsOn).Except(n.DependsOn, StringComparer.Ordinal).ToArray();
            return extra.Length == 0 ? n : n with { DependsOn = [.. n.DependsOn, .. extra] };
        });

        return Create(rewired.Concat(change.AddedNodes));
    }

    public WorkflowNode Get(string id) =>
        _byId.TryGetValue(id, out var node) ? node : throw new KeyNotFoundException($"Unknown node '{id}'.");

    public bool Contains(string id) => _byId.ContainsKey(id);

    public IReadOnlyList<WorkflowNode> Dependents(string id)
    {
        Get(id);
        return Nodes.Where(n => n.DependsOn.Contains(id, StringComparer.Ordinal)).ToArray();
    }

    /// <summary>Every node upstream of <paramref name="id"/>, in topological order. These are a node's inputs.</summary>
    public IReadOnlyList<WorkflowNode> TransitiveDependencies(string id)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>(Get(id).DependsOn);
        while (pending.TryDequeue(out var current))
        {
            if (found.Add(current))
            {
                foreach (var dependency in Get(current).DependsOn)
                {
                    pending.Enqueue(dependency);
                }
            }
        }

        return TopologicalOrder.Where(n => found.Contains(n.Id)).ToArray();
    }

    /// <summary>Every node downstream of <paramref name="id"/>, in topological order. Used to invalidate work on re-plan.</summary>
    public IReadOnlyList<WorkflowNode> TransitiveDependents(string id)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>([id]);
        while (pending.TryDequeue(out var current))
        {
            foreach (var dependent in Dependents(current))
            {
                if (found.Add(dependent.Id))
                {
                    pending.Enqueue(dependent.Id);
                }
            }
        }

        return TopologicalOrder.Where(n => found.Contains(n.Id)).ToArray();
    }

    private static List<string> Validate(WorkflowNode[] nodes)
    {
        var errors = new List<string>();
        if (nodes.Length == 0)
        {
            errors.Add("A workflow graph needs at least one node.");
            return errors;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            if (!ids.Add(node.Id))
            {
                errors.Add($"Duplicate node id '{node.Id}'.");
            }
        }

        foreach (var node in nodes)
        {
            foreach (var dependency in node.DependsOn.Where(d => !ids.Contains(d)))
            {
                errors.Add($"Node '{node.Id}' depends on unknown node '{dependency}'.");
            }
        }

        errors.AddRange(FindCycles(nodes, ids));

        var entries = nodes.Count(n => n.DependsOn.Count == 0);
        if (entries != 1)
        {
            errors.Add($"A workflow graph needs exactly one entry node (no dependencies); found {entries}.");
        }

        return errors;
    }

    // Depth-first search along DependsOn edges; a dependency already on the stack closes a cycle.
    private static List<string> FindCycles(WorkflowNode[] nodes, HashSet<string> ids)
    {
        var byId = nodes.GroupBy(n => n.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var done = new HashSet<string>(StringComparer.Ordinal);
        var stack = new List<string>();
        var cycles = new List<string>();

        void Visit(string id)
        {
            if (done.Contains(id))
            {
                return;
            }

            var onStack = stack.IndexOf(id);
            if (onStack >= 0)
            {
                cycles.Add("Cycle detected: " + string.Join(" -> ", stack.Skip(onStack).Append(id)));
                return;
            }

            stack.Add(id);
            foreach (var dependency in byId[id].DependsOn.Where(ids.Contains))
            {
                Visit(dependency);
            }

            stack.RemoveAt(stack.Count - 1);
            done.Add(id);
        }

        foreach (var node in byId.Values)
        {
            Visit(node.Id);
        }

        return cycles;
    }

    private static List<IReadOnlyList<WorkflowNode>> BuildLayers(WorkflowNode[] nodes)
    {
        var layers = new List<IReadOnlyList<WorkflowNode>>();
        var placed = new HashSet<string>(StringComparer.Ordinal);
        while (placed.Count < nodes.Length)
        {
            var layer = nodes.Where(n => !placed.Contains(n.Id) && n.DependsOn.All(placed.Contains)).ToArray();
            layers.Add(layer);
            placed.UnionWith(layer.Select(n => n.Id));
        }

        return layers;
    }
}

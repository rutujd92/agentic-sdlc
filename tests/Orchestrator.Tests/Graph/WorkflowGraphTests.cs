using Orchestrator.Core.Graph;

namespace Orchestrator.Tests.Graph;

public class WorkflowGraphTests
{
    private static WorkflowNode Node(string id, params string[] dependsOn) =>
        new(id, NodeKind.Custom) { DependsOn = dependsOn };

    [Fact]
    public void TopologicalOrder_places_every_node_after_its_dependencies()
    {
        var graph = WorkflowGraph.Create(
        [
            Node("validate", "implement", "tests"),
            Node("implement", "design"),
            Node("tests", "design"),
            Node("design", "requirements"),
            Node("requirements"),
        ]);

        var order = graph.TopologicalOrder.Select(n => n.Id).ToList();

        Assert.Equal(5, order.Count);
        foreach (var node in graph.Nodes)
        {
            Assert.All(node.DependsOn, dep => Assert.True(order.IndexOf(dep) < order.IndexOf(node.Id), $"{dep} must precede {node.Id}"));
        }
    }

    [Fact]
    public void TopologicalOrder_is_deterministic_by_declaration_order()
    {
        WorkflowNode[] nodes = [Node("root"), Node("b", "root"), Node("a", "root"), Node("join", "a", "b")];

        Assert.Equal(["root", "b", "a", "join"], WorkflowGraph.Create(nodes).TopologicalOrder.Select(n => n.Id));
    }

    [Fact]
    public void Layers_group_nodes_that_can_run_in_parallel()
    {
        var graph = WorkflowGraph.Create([Node("root"), Node("x", "root"), Node("y", "root"), Node("join", "x", "y")]);

        var layers = graph.Layers.Select(l => l.Select(n => n.Id).ToArray()).ToArray();

        Assert.Equal([["root"], ["x", "y"], ["join"]], layers);
    }

    [Fact]
    public void Entry_is_the_single_node_without_dependencies()
    {
        Assert.Equal("root", WorkflowGraph.Create([Node("root"), Node("a", "root")]).Entry.Id);
    }

    [Fact]
    public void Dependents_and_transitive_dependents_follow_edges_downstream()
    {
        var graph = WorkflowGraph.Create(
        [
            Node("req"), Node("design", "req"), Node("impl", "design"), Node("docs", "design"), Node("validate", "impl", "docs"), Node("other", "req"),
        ]);

        Assert.Equal(["impl", "docs"], graph.Dependents("design").Select(n => n.Id));
        Assert.Equal(["impl", "docs", "validate"], graph.TransitiveDependents("design").Select(n => n.Id));
        Assert.Empty(graph.TransitiveDependents("validate"));
    }

    [Fact]
    public void Create_rejects_empty_graph()
    {
        var ex = Assert.Throws<InvalidWorkflowGraphException>(() => WorkflowGraph.Create([]));

        Assert.Contains(ex.Errors, e => e.Contains("at least one node", StringComparison.Ordinal));
    }

    [Fact]
    public void Create_rejects_duplicate_ids()
    {
        var ex = Assert.Throws<InvalidWorkflowGraphException>(() => WorkflowGraph.Create([Node("a"), Node("a")]));

        Assert.Contains(ex.Errors, e => e.Contains("Duplicate node id 'a'", StringComparison.Ordinal));
    }

    [Fact]
    public void Create_rejects_unknown_dependency()
    {
        var ex = Assert.Throws<InvalidWorkflowGraphException>(() => WorkflowGraph.Create([Node("a"), Node("b", "ghost")]));

        Assert.Contains(ex.Errors, e => e.Contains("'b' depends on unknown node 'ghost'", StringComparison.Ordinal));
    }

    [Fact]
    public void Create_rejects_cycle_and_reports_its_path()
    {
        var ex = Assert.Throws<InvalidWorkflowGraphException>(() =>
            WorkflowGraph.Create([Node("root"), Node("a", "root", "c"), Node("b", "a"), Node("c", "b")]));

        Assert.Contains(ex.Errors, e => e.Contains("Cycle detected: a -> c -> b -> a", StringComparison.Ordinal));
    }

    [Fact]
    public void Create_rejects_self_dependency()
    {
        var ex = Assert.Throws<InvalidWorkflowGraphException>(() => WorkflowGraph.Create([Node("root"), Node("a", "root", "a")]));

        Assert.Contains(ex.Errors, e => e.Contains("Cycle detected: a -> a", StringComparison.Ordinal));
    }

    [Fact]
    public void Create_rejects_multiple_entry_nodes()
    {
        var ex = Assert.Throws<InvalidWorkflowGraphException>(() => WorkflowGraph.Create([Node("a"), Node("b")]));

        Assert.Contains(ex.Errors, e => e.Contains("exactly one entry node", StringComparison.Ordinal));
    }

    [Fact]
    public void Create_reports_all_errors_at_once()
    {
        var ex = Assert.Throws<InvalidWorkflowGraphException>(() =>
            WorkflowGraph.Create([Node("a"), Node("a"), Node("b", "ghost")]));

        Assert.True(ex.Errors.Count >= 2);
    }
}

using Orchestrator.Core.Events;
using Orchestrator.Core.Execution;
using Orchestrator.Core.Governance;
using Orchestrator.Core.Graph;
using Orchestrator.Core.Planning;
using Orchestrator.Core.State;
using Orchestrator.Tests.Fakes;

namespace Orchestrator.Tests.Planning;

public class ReplanningTests
{
    private const string RunId = "run-p";
    private readonly InMemoryEventStore _events = new();
    private readonly InMemoryArtifactStore _artifacts = new();
    private readonly CancellationToken _ct = CancellationToken.None;

    // requirements -> design -> {implement, docs} -> merge (approval)
    private static WorkflowGraph Graph() => WorkflowGraph.Create(
    [
        new("requirements", NodeKind.Requirements),
        new("design", NodeKind.Design) { DependsOn = ["requirements"] },
        new("implement", NodeKind.Implement) { DependsOn = ["design"] },
        new("docs", NodeKind.Docs) { DependsOn = ["design"] },
        new("merge", NodeKind.Merge) { DependsOn = ["implement", "docs"], RequiresApproval = true, Risk = RiskLevel.High },
    ]);

    private OrchestrationEngine Engine() => new(_events, _artifacts, TimeProvider.System);

    private ReplanService Replan() => new(_events, TimeProvider.System);

    private static WorkflowDefinition Workflow(FakeExecutor executor) => new(Graph(), _ => executor);

    private async Task<IReadOnlyList<RunEvent>> EventsAsync() => await _events.ReadAsync(RunId, _ct);

    private static int Runs(FakeExecutor executor, string node) => executor.Calls.Count(c => c.NodeId == node);

    [Fact]
    public async Task Revised_requirements_rerun_with_guidance_and_cascade_to_changed_dependents()
    {
        var executor = new FakeExecutor { EchoFeedback = true };
        await Engine().RunAsync(RunId, "make links safer", Workflow(executor), _ct);

        await Replan().ReviseAsync(RunId, "requirements", "lead@example.com", "Safer means: block known-malicious domains via a denylist.", _ct);
        var state = await Engine().ResumeAsync(RunId, Workflow(executor), _ct);

        Assert.Equal(RunStatus.Paused, state.Status);
        Assert.Equal(NodeStatus.AwaitingApproval, state.Nodes["merge"].Status);
        Assert.Equal(2, Runs(executor, "requirements"));
        Assert.Equal("Safer means: block known-malicious domains via a denylist.", executor.Calls.Last(c => c.NodeId == "requirements").Feedback);
        Assert.Equal(2, Runs(executor, "design"));
        Assert.Contains("denylist", executor.Contexts["design"].Inputs["requirements"].Content, StringComparison.Ordinal);

        var invalidated = (await EventsAsync()).Where(e => e.Type == RunEventType.NodeInvalidated).Select(e => e.NodeId!).ToArray();
        Assert.Equal(["requirements", "design"], invalidated);
    }

    [Fact]
    public async Task Revision_that_does_not_change_output_keeps_dependents()
    {
        var executor = new FakeExecutor();
        await Engine().RunAsync(RunId, "cr", Workflow(executor), _ct);

        await Replan().ReviseAsync(RunId, "design", "lead@example.com", "Double-check the API shape.", _ct);
        await Engine().ResumeAsync(RunId, Workflow(executor), _ct);

        Assert.Equal(2, Runs(executor, "design"));
        Assert.Equal(1, Runs(executor, "implement"));
        Assert.Equal(1, Runs(executor, "docs"));
        Assert.Equal(1, Runs(executor, "requirements"));
    }

    [Fact]
    public async Task Changed_upstream_output_invalidates_pending_approval_so_it_is_requested_again()
    {
        var executor = new FakeExecutor { EchoFeedback = true, Outputs = { ["implement"] = "v1" } };
        await Engine().RunAsync(RunId, "cr", Workflow(executor), _ct);

        await Replan().ReviseAsync(RunId, "implement", "lead@example.com", "Use a typed result.", _ct);
        await Engine().ResumeAsync(RunId, Workflow(executor), _ct);

        var requests = (await EventsAsync()).Where(e => e.Type == RunEventType.ApprovalRequested && e.NodeId == "merge").ToArray();
        Assert.Equal(2, requests.Length);
        Assert.Contains(await EventsAsync(), e => e.Type == RunEventType.NodeInvalidated && e.NodeId == "merge");
    }

    [Fact]
    public async Task Revise_is_rejected_for_pending_nodes_and_unknown_nodes()
    {
        await Engine().RunAsync(RunId, "cr", Workflow(new FakeExecutor()), _ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Replan().ReviseAsync(RunId, "merge", "x@example.com", "g", _ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Replan().ReviseAsync(RunId, "ghost", "x@example.com", "g", _ct));
    }

    [Fact]
    public async Task Planner_can_add_a_migration_node_which_requires_approval()
    {
        var change = new GraphChange(
            [new WorkflowNode("migration", NodeKind.Migration) { DependsOn = ["design"], Description = "Add clicks table" }],
            [new DependencyEdge("implement", "migration")],
            "Click analytics needs a clicks table.");
        var executor = new FakeExecutor { Plans = { ["design"] = change } };

        var state = await Engine().RunAsync(RunId, "add analytics", Workflow(executor), _ct);

        Assert.Equal(RunStatus.Paused, state.Status);
        Assert.Equal(NodeStatus.AwaitingApproval, state.Nodes["migration"].Status);
        Assert.Equal(NodeStatus.Pending, state.Nodes["implement"].Status);
        Assert.Equal(NodeStatus.Succeeded, state.Nodes["docs"].Status);
        var replanned = (await EventsAsync()).Single(e => e.Type == RunEventType.Replanned);
        Assert.Equal("design", replanned.NodeId);
        Assert.Equal("Click analytics needs a clicks table.", replanned.Message);
        Assert.Equal("migration", replanned.Data["addedNodes"]);

        await new ApprovalService(_events, TimeProvider.System).GrantAsync(RunId, "migration", "dba@example.com", "ok", _ct);
        var resumed = await Engine().ResumeAsync(RunId, Workflow(executor), _ct);

        Assert.Equal(NodeStatus.Succeeded, resumed.Nodes["migration"].Status);
        Assert.Equal(NodeStatus.Succeeded, resumed.Nodes["implement"].Status);
        Assert.True(executor.Timings["migration"].End <= executor.Timings["implement"].Start);
        Assert.Contains("migration", executor.Contexts["implement"].Inputs.Keys);
    }

    [Fact]
    public async Task Invalid_plan_fails_the_attempt_with_feedback()
    {
        var cyclic = new GraphChange(
            [new WorkflowNode("extra", NodeKind.Custom) { DependsOn = ["implement"] }],
            [new DependencyEdge("implement", "extra")],
            "bad plan");
        var executor = new FakeExecutor { Plans = { ["design"] = cyclic } };

        var state = await Engine().RunAsync(RunId, "cr", Workflow(executor), _ct);

        Assert.Equal(NodeStatus.Failed, state.Nodes["design"].Status);
        Assert.Contains("Cycle detected", state.Nodes["design"].Error, StringComparison.Ordinal);
        Assert.DoesNotContain(await EventsAsync(), e => e.Type == RunEventType.Replanned);
    }

    [Fact]
    public async Task Plan_cannot_rewire_a_node_that_already_ran()
    {
        var late = new GraphChange([new WorkflowNode("audit", NodeKind.Custom) { DependsOn = ["design"] }], [new DependencyEdge("requirements", "audit")], "late");
        var executor = new FakeExecutor { Plans = { ["implement"] = late } };

        var state = await Engine().RunAsync(RunId, "cr", Workflow(executor), _ct);

        Assert.Equal(NodeStatus.Failed, state.Nodes["implement"].Status);
        Assert.Contains("already", state.Nodes["implement"].Error, StringComparison.Ordinal);
    }

    [Fact]
    public void GraphChange_governance_forces_approval_on_high_impact_nodes()
    {
        var change = new GraphChange(
        [
            new WorkflowNode("m", NodeKind.Migration),
            new WorkflowNode("risky", NodeKind.Custom) { Risk = RiskLevel.High },
            new WorkflowNode("note", NodeKind.Docs),
        ], [], "r");

        var governed = change.Governed();

        Assert.True(governed.AddedNodes.Single(n => n.Id == "m").RequiresApproval);
        Assert.True(governed.AddedNodes.Single(n => n.Id == "risky").RequiresApproval);
        Assert.False(governed.AddedNodes.Single(n => n.Id == "note").RequiresApproval);
    }
}

public class ParallelInvalidationTests
{
    [Fact]
    public async Task Join_node_is_invalidated_once_when_parallel_inputs_change_together()
    {
        var events = new InMemoryEventStore();
        var engine = new OrchestrationEngine(events, new InMemoryArtifactStore(), TimeProvider.System);
        var graph = WorkflowGraph.Create(
        [
            new("root", NodeKind.Custom),
            new("x", NodeKind.Custom) { DependsOn = ["root"] },
            new("y", NodeKind.Custom) { DependsOn = ["root"] },
            new("join", NodeKind.Merge) { DependsOn = ["x", "y"], RequiresApproval = true },
        ]);
        var executor = new FakeExecutor { EchoFeedback = true, Delay = TimeSpan.FromMilliseconds(30), Outputs = { ["x"] = "x", ["y"] = "y" } };
        var workflow = new WorkflowDefinition(graph, _ => executor);
        await engine.RunAsync("r", "cr", workflow, CancellationToken.None);

        await new ReplanService(events, TimeProvider.System).ReviseAsync("r", "root", "a@example.com", "new direction", CancellationToken.None);
        var executor2 = new FakeExecutor { Delay = TimeSpan.FromMilliseconds(30), Outputs = { ["root"] = "root2", ["x"] = "x2", ["y"] = "y2" } };
        await engine.ResumeAsync("r", workflow with { ExecutorFor = _ => executor2 }, CancellationToken.None);

        var all = await events.ReadAsync("r", CancellationToken.None);
        Assert.Single(all, e => e.Type == RunEventType.NodeInvalidated && e.NodeId == "join");
    }
}

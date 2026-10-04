using Orchestrator.Core.Events;
using Orchestrator.Core.Execution;
using Orchestrator.Core.Graph;
using Orchestrator.Core.State;
using Orchestrator.Tests.Fakes;

namespace Orchestrator.Tests.Execution;

public class OrchestrationEngineTests
{
    private const string RunId = "run-1";
    private readonly InMemoryEventStore _events = new();
    private readonly InMemoryArtifactStore _artifacts = new();
    private readonly CancellationToken _ct = CancellationToken.None;

    private static WorkflowNode Node(string id, params string[] dependsOn) => new(id, NodeKind.Custom) { DependsOn = dependsOn };

    private static WorkflowGraph Diamond() =>
        WorkflowGraph.Create([Node("root"), Node("x", "root"), Node("y", "root"), Node("z", "root"), Node("join", "x", "y", "z")]);

    private OrchestrationEngine Engine(int maxParallelism = 4) =>
        new(_events, _artifacts, TimeProvider.System, new EngineOptions { MaxParallelism = maxParallelism });

    private Task<RunState> RunAsync(WorkflowGraph graph, FakeExecutor executor, Func<WorkflowNode, IReadOnlyList<IGate>>? entry = null, Func<WorkflowNode, IReadOnlyList<IGate>>? exit = null) =>
        Engine().RunAsync(RunId, "add a feature", new WorkflowDefinition(graph, _ => executor)
        {
            EntryGatesFor = entry ?? (_ => []),
            ExitGatesFor = exit ?? (_ => []),
        }, _ct);

    [Fact]
    public async Task Runs_sequential_nodes_in_dependency_order()
    {
        var executor = new FakeExecutor();

        var state = await RunAsync(WorkflowGraph.Create([Node("a"), Node("b", "a"), Node("c", "b")]), executor);

        Assert.Equal(["a", "b", "c"], executor.Started);
        Assert.Equal(RunStatus.Succeeded, state.Status);
        Assert.All(state.Nodes.Values, n => Assert.Equal(NodeStatus.Succeeded, n.Status));
    }

    [Fact]
    public async Task Runs_independent_nodes_in_parallel_and_join_waits_for_all()
    {
        var executor = new FakeExecutor { Delay = TimeSpan.FromMilliseconds(150) };

        await RunAsync(Diamond(), executor);

        Assert.Equal(3, executor.PeakConcurrency);
        var joinStart = executor.Timings["join"].Start;
        Assert.All(new[] { "x", "y", "z" }, id => Assert.True(executor.Timings[id].End <= joinStart, $"{id} must finish before join starts"));
    }

    [Fact]
    public async Task Respects_max_parallelism()
    {
        var executor = new FakeExecutor { Delay = TimeSpan.FromMilliseconds(50) };

        await Engine(maxParallelism: 2).RunAsync(RunId, "cr", new WorkflowDefinition(Diamond(), _ => executor), _ct);

        Assert.Equal(2, executor.PeakConcurrency);
    }

    [Fact]
    public async Task Passes_all_upstream_artifacts_as_inputs()
    {
        var executor = new FakeExecutor();

        await RunAsync(Diamond(), executor);

        var inputs = executor.Contexts["join"].Inputs;
        Assert.Equal(["root", "x", "y", "z"], inputs.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(Artifact.Create("x", "output of x").Hash, inputs["x"].Hash);
        Assert.Equal("add a feature", executor.Contexts["root"].ChangeRequest);
        Assert.Empty(executor.Contexts["root"].Inputs);
    }

    [Fact]
    public async Task Records_input_and_output_hashes_for_lineage()
    {
        await RunAsync(Diamond(), new FakeExecutor());

        var events = await _events.ReadAsync(RunId, _ct);
        var joined = events.Single(e => e.Type == RunEventType.NodeSucceeded && e.NodeId == "join");
        Assert.Equal(Artifact.Create("join", "output of join").Hash, joined.Data["outputHash"]);
        foreach (var upstream in new[] { "root", "x", "y", "z" })
        {
            Assert.Contains($"{upstream}={Artifact.Create(upstream, $"output of {upstream}").Hash}", joined.Data["inputHashes"], StringComparison.Ordinal);
        }

        Assert.Equal("agent:fake", joined.Actor);
        Assert.Equal("join done", joined.Message);
        Assert.NotNull(await _artifacts.GetAsync(RunId, "join", _ct));
    }

    [Fact]
    public async Task Exit_gate_failure_fails_node_and_skips_downstream()
    {
        var executor = new FakeExecutor();
        var gate = new FakeGate("tests-green", ctx => ctx.Node.Id != "y", "2 tests failed");

        var state = await RunAsync(Diamond(), executor, exit: _ => [gate]);

        Assert.Equal(RunStatus.Failed, state.Status);
        Assert.Equal(NodeStatus.Failed, state.Nodes["y"].Status);
        Assert.Contains("2 tests failed", state.Nodes["y"].Error, StringComparison.Ordinal);
        Assert.Equal(NodeStatus.Skipped, state.Nodes["join"].Status);
        Assert.DoesNotContain("join", executor.Started);
        var events = await _events.ReadAsync(RunId, _ct);
        Assert.Contains(events, e => e.Type == RunEventType.GateFailed && e.NodeId == "y" && e.Data["gate"] == "tests-green" && e.Data["phase"] == "exit");
    }

    [Fact]
    public async Task Independent_branches_finish_when_a_sibling_fails()
    {
        var executor = new FakeExecutor { FailingNodes = ["x"] };

        var state = await RunAsync(Diamond(), executor);

        Assert.Equal(NodeStatus.Failed, state.Nodes["x"].Status);
        Assert.Equal(NodeStatus.Succeeded, state.Nodes["y"].Status);
        Assert.Equal(NodeStatus.Succeeded, state.Nodes["z"].Status);
        Assert.Equal(NodeStatus.Skipped, state.Nodes["join"].Status);
    }

    [Fact]
    public async Task Entry_gate_failure_fails_node_without_running_executor()
    {
        var executor = new FakeExecutor();
        var gate = new FakeGate("has-requirements", ctx => ctx.Node.Id != "x", "requirements missing");

        var state = await RunAsync(Diamond(), executor, entry: _ => [gate]);

        Assert.Equal(NodeStatus.Failed, state.Nodes["x"].Status);
        Assert.DoesNotContain("x", executor.Started);
        Assert.Contains(await _events.ReadAsync(RunId, _ct), e => e.Type == RunEventType.GateFailed && e.Data["phase"] == "entry");
    }

    [Fact]
    public async Task Executor_exception_fails_node_with_message()
    {
        var state = await RunAsync(Diamond(), new FakeExecutor { ThrowingNodes = ["z"] });

        Assert.Equal(NodeStatus.Failed, state.Nodes["z"].Status);
        Assert.Contains("boom in z", state.Nodes["z"].Error, StringComparison.Ordinal);
        Assert.Equal(RunStatus.Failed, state.Status);
    }

    [Fact]
    public async Task State_rebuilt_from_event_log_equals_live_state()
    {
        var live = await RunAsync(Diamond(), new FakeExecutor { FailingNodes = ["y"] });

        var rebuilt = RunState.Rebuild(await _events.ReadAsync(RunId, _ct));

        Assert.Equal(live.Status, rebuilt.Status);
        Assert.Equal(live.Nodes.OrderBy(n => n.Key, StringComparer.Ordinal), rebuilt.Nodes.OrderBy(n => n.Key, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Event_log_is_ordered_and_complete()
    {
        await RunAsync(Diamond(), new FakeExecutor());

        var events = await _events.ReadAsync(RunId, _ct);

        Assert.Equal(Enumerable.Range(1, events.Count).Select(i => (long)i), events.Select(e => e.Sequence));
        Assert.Equal(RunEventType.RunStarted, events[0].Type);
        Assert.Equal(RunEventType.RunCompleted, events[^1].Type);
        Assert.Equal(5, events.Count(e => e.Type == RunEventType.NodeStarted));
        Assert.All(events, e => Assert.Equal(RunId, e.RunId));
    }
}

using Orchestrator.Core.Events;
using Orchestrator.Core.Execution;
using Orchestrator.Core.Graph;
using Orchestrator.Core.State;
using Orchestrator.Tests.Fakes;

namespace Orchestrator.Tests.Execution;

public class ReliabilityTests
{
    private const string RunId = "run-r";
    private static readonly RetryPolicy ThreeAttempts = new(3, TimeSpan.FromMilliseconds(1));
    private readonly InMemoryEventStore _events = new();
    private readonly InMemoryArtifactStore _artifacts = new();
    private readonly FakeCheckpointStore _checkpoints = new();
    private readonly ManualStopSignal _stop = new();
    private readonly CancellationToken _ct = CancellationToken.None;

    private static WorkflowNode Node(string id, params string[] dependsOn) => new(id, NodeKind.Custom) { DependsOn = dependsOn };

    private static WorkflowGraph Chain() => WorkflowGraph.Create([Node("a"), Node("b", "a"), Node("c", "b")]);

    private OrchestrationEngine Engine(EngineOptions? options = null) =>
        new(_events, _artifacts, TimeProvider.System, options, _checkpoints, _stop);

    private static WorkflowDefinition Workflow(WorkflowGraph graph, INodeExecutor primary, RetryPolicy? retry = null, IReadOnlyList<INodeExecutor>? fallbacks = null, IGate? exitGate = null) =>
        new(graph, _ => primary)
        {
            RetryPolicyFor = _ => retry ?? RetryPolicy.None,
            FallbacksFor = _ => fallbacks ?? [],
            ExitGatesFor = _ => exitGate is null ? [] : [exitGate],
        };

    private async Task<IReadOnlyList<RunEvent>> EventsAsync() => await _events.ReadAsync(RunId, _ct);

    [Fact]
    public async Task Retries_failed_node_and_feeds_back_previous_error()
    {
        var executor = new FakeExecutor { FailFirst = { ["b"] = 2 } };

        var state = await Engine().RunAsync(RunId, "cr", Workflow(Chain(), executor, ThreeAttempts), _ct);

        Assert.Equal(RunStatus.Succeeded, state.Status);
        Assert.Equal(3, state.Nodes["b"].Attempts);
        var bCalls = executor.Calls.Where(c => c.NodeId == "b").ToArray();
        Assert.Equal([1, 2, 3], bCalls.Select(c => c.Attempt));
        Assert.Null(bCalls[0].Feedback);
        Assert.Equal("b could not complete", bCalls[1].Feedback);
        Assert.Equal(2, (await EventsAsync()).Count(e => e.Type == RunEventType.RetryScheduled && e.NodeId == "b"));
    }

    [Fact]
    public async Task Retry_backoff_grows_exponentially()
    {
        var executor = new FakeExecutor { FailFirst = { ["a"] = 2 } };

        await Engine().RunAsync(RunId, "cr", Workflow(Chain(), executor, new RetryPolicy(3, TimeSpan.FromMilliseconds(5), 2)), _ct);

        Assert.Equal(["5", "10"], (await EventsAsync()).Where(e => e.Type == RunEventType.RetryScheduled).Select(e => e.Data["delayMs"]));
    }

    [Fact]
    public async Task Exit_gate_failure_is_retried()
    {
        var calls = 0;
        var gate = new FakeGate("tests-green", ctx => ctx.Node.Id != "b" || ++calls > 1, "1 test failed");

        var state = await Engine().RunAsync(RunId, "cr", Workflow(Chain(), new FakeExecutor(), ThreeAttempts, exitGate: gate), _ct);

        Assert.Equal(RunStatus.Succeeded, state.Status);
        Assert.Equal(2, state.Nodes["b"].Attempts);
    }

    [Fact]
    public async Task Uses_fallback_executor_after_retries_are_exhausted()
    {
        var primary = new FakeExecutor { FailingNodes = ["b"] };
        var fallback = new FakeExecutor { Actor = "agent:fallback" };

        var state = await Engine().RunAsync(RunId, "cr", Workflow(Chain(), primary, ThreeAttempts, [fallback]), _ct);

        Assert.Equal(RunStatus.Succeeded, state.Status);
        Assert.Equal(3, primary.Calls.Count(c => c.NodeId == "b"));
        Assert.Single(fallback.Calls, c => c.NodeId == "b");
        var events = await EventsAsync();
        Assert.Contains(events, e => e.Type == RunEventType.FallbackUsed && e.NodeId == "b");
        Assert.Equal("agent:fallback", events.Single(e => e.Type == RunEventType.NodeSucceeded && e.NodeId == "b").Actor);
    }

    [Fact]
    public async Task Creates_checkpoint_after_each_successful_node()
    {
        await Engine().RunAsync(RunId, "cr", Workflow(Chain(), new FakeExecutor()), _ct);

        Assert.Equal(3, _checkpoints.Created.Count);
        Assert.Equal(3, (await EventsAsync()).Count(e => e.Type == RunEventType.CheckpointCreated));
    }

    [Fact]
    public async Task Unrecoverable_failure_rolls_back_to_last_checkpoint()
    {
        var executor = new FakeExecutor { FailingNodes = ["c"] };
        var fallback = new FakeExecutor { FailingNodes = ["c"] };

        var state = await Engine().RunAsync(RunId, "cr", Workflow(Chain(), executor, ThreeAttempts, [fallback]), _ct);

        Assert.Equal(RunStatus.RolledBack, state.Status);
        Assert.Equal(NodeStatus.Failed, state.Nodes["c"].Status);
        Assert.Equal([_checkpoints.Created[^1]], _checkpoints.RolledBackTo);
        Assert.Contains("after b", _checkpoints.RolledBackTo[0], StringComparison.Ordinal);
        var rolledBack = (await EventsAsync()).Single(e => e.Type == RunEventType.RolledBack);
        Assert.Equal(_checkpoints.Created[^1], rolledBack.Data["checkpointId"]);
    }

    [Fact]
    public async Task Stop_request_halts_before_next_node_and_leaves_run_resumable()
    {
        var executor = new FakeExecutor { OnExecuted = ctx => { if (ctx.Node.Id == "a") { _stop.Request(); } } };

        var stopped = await Engine().RunAsync(RunId, "cr", Workflow(Chain(), executor), _ct);

        Assert.Equal(RunStatus.Stopped, stopped.Status);
        Assert.Equal(NodeStatus.Succeeded, stopped.Nodes["a"].Status);
        Assert.Equal(NodeStatus.Pending, stopped.Nodes["b"].Status);
        Assert.Contains(await EventsAsync(), e => e.Type == RunEventType.SafeStopped && e.Message!.Contains("Stop requested", StringComparison.Ordinal));
        Assert.Equal(RunStatus.Stopped, RunState.Rebuild(await EventsAsync()).Status);

        _stop.Reset();
        var resumed = await Engine().ResumeAsync(RunId, Workflow(Chain(), executor), _ct);

        Assert.Equal(RunStatus.Succeeded, resumed.Status);
        Assert.Single(executor.Calls, c => c.NodeId == "a");
        Assert.Equal("output of a", executor.Contexts["b"].Inputs["a"].Content);
        Assert.Equal("cr", executor.Contexts["b"].ChangeRequest);
        var events = await EventsAsync();
        Assert.Equal(Enumerable.Range(1, events.Count).Select(i => (long)i), events.Select(e => e.Sequence));
    }

    [Fact]
    public async Task Attempt_budget_triggers_safe_stop()
    {
        var state = await Engine(new EngineOptions { MaxTotalAttempts = 2 }).RunAsync(RunId, "cr", Workflow(Chain(), new FakeExecutor()), _ct);

        Assert.Equal(RunStatus.Stopped, state.Status);
        Assert.Equal(NodeStatus.Pending, state.Nodes["c"].Status);
        Assert.Contains(await EventsAsync(), e => e.Type == RunEventType.SafeStopped && e.Message!.Contains("budget", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Resume_of_finished_run_is_rejected()
    {
        await Engine().RunAsync(RunId, "cr", Workflow(Chain(), new FakeExecutor()), _ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Engine().ResumeAsync(RunId, Workflow(Chain(), new FakeExecutor()), _ct));
    }
}

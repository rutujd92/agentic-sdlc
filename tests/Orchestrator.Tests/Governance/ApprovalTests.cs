using Orchestrator.Core.Events;
using Orchestrator.Core.Execution;
using Orchestrator.Core.Governance;
using Orchestrator.Core.Graph;
using Orchestrator.Core.State;
using Orchestrator.Tests.Fakes;

namespace Orchestrator.Tests.Governance;

public class ApprovalTests
{
    private const string RunId = "run-g";
    private readonly InMemoryEventStore _events = new();
    private readonly InMemoryArtifactStore _artifacts = new();
    private readonly FakeCheckpointStore _checkpoints = new();
    private readonly CancellationToken _ct = CancellationToken.None;

    private static readonly PolicyRules Rules = new()
    {
        AllowedPaths = ["src/**"],
        ApprovalPaths = ["**/Migrations/**"],
        SecretPatterns = ["(?i)password\\s*=\\s*[^;\\s\"]{4,}"],
    };

    private static WorkflowGraph Graph() => WorkflowGraph.Create(
    [
        new("design", NodeKind.Design),
        new("implement", NodeKind.Implement) { DependsOn = ["design"] },
        new("merge", NodeKind.Merge) { DependsOn = ["implement"], RequiresApproval = true, Risk = RiskLevel.High },
    ]);

    private OrchestrationEngine Engine() => new(_events, _artifacts, TimeProvider.System, null, _checkpoints);

    private ApprovalService Approvals() => new(_events, TimeProvider.System);

    private static WorkflowDefinition Workflow(FakeExecutor executor, RetryPolicy? retry = null) => new(Graph(), _ => executor)
    {
        ExitGatesFor = n => n.Kind == NodeKind.Implement ? [new PolicyGate(new PolicyEngine(Rules))] : [],
        RetryPolicyFor = _ => retry ?? RetryPolicy.None,
    };

    private async Task<IReadOnlyList<RunEvent>> EventsAsync() => await _events.ReadAsync(RunId, _ct);

    [Fact]
    public async Task High_impact_node_pauses_run_before_executing()
    {
        var executor = new FakeExecutor();

        var state = await Engine().RunAsync(RunId, "cr", Workflow(executor), _ct);

        Assert.Equal(RunStatus.Paused, state.Status);
        Assert.Equal(NodeStatus.AwaitingApproval, state.Nodes["merge"].Status);
        Assert.DoesNotContain("merge", executor.Started);
        var request = (await EventsAsync()).Single(e => e.Type == RunEventType.ApprovalRequested);
        Assert.Equal("merge", request.NodeId);
        Assert.Equal("pre", request.Data["phase"]);
        Assert.Equal(RunStatus.Paused, RunState.Rebuild(await EventsAsync()).Status);
    }

    [Fact]
    public async Task Granted_approval_resumes_and_executes_the_node()
    {
        var executor = new FakeExecutor();
        await Engine().RunAsync(RunId, "cr", Workflow(executor), _ct);

        await Approvals().GrantAsync(RunId, "merge", "lead@example.com", "LGTM", _ct);
        var state = await Engine().ResumeAsync(RunId, Workflow(executor), _ct);

        Assert.Equal(RunStatus.Succeeded, state.Status);
        Assert.Contains("merge", executor.Started);
        var granted = (await EventsAsync()).Single(e => e.Type == RunEventType.ApprovalGranted);
        Assert.Equal("human:lead@example.com", granted.Actor);
        Assert.Equal("LGTM", granted.Message);
    }

    [Fact]
    public async Task Rejected_approval_fails_node_and_rolls_back()
    {
        var executor = new FakeExecutor();
        await Engine().RunAsync(RunId, "cr", Workflow(executor), _ct);

        await Approvals().RejectAsync(RunId, "merge", "lead@example.com", "Not before the release freeze", _ct);
        var state = await Engine().ResumeAsync(RunId, Workflow(executor), _ct);

        Assert.Equal(RunStatus.RolledBack, state.Status);
        Assert.Equal(NodeStatus.Failed, state.Nodes["merge"].Status);
        Assert.Contains("Not before the release freeze", state.Nodes["merge"].Error, StringComparison.Ordinal);
        Assert.DoesNotContain("merge", executor.Started);
        Assert.Single(_checkpoints.RolledBackTo);
    }

    [Fact]
    public async Task Policy_flagged_output_pauses_for_review_and_approval_completes_without_rerun()
    {
        var migration = Diffs.File("src/Infra/Migrations/20261004_AddClicks.cs", "CreateTable(\"clicks\")");
        var executor = new FakeExecutor { Outputs = { ["implement"] = migration } };

        var paused = await Engine().RunAsync(RunId, "cr", Workflow(executor), _ct);

        Assert.Equal(RunStatus.Paused, paused.Status);
        Assert.Equal(NodeStatus.AwaitingApproval, paused.Nodes["implement"].Status);
        var events = await EventsAsync();
        Assert.Contains(events, e => e.Type == RunEventType.PolicyViolation && e.NodeId == "implement" && e.Data["outcome"] == "RequireApproval");
        Assert.Equal("output", events.Single(e => e.Type == RunEventType.ApprovalRequested).Data["phase"]);

        await Approvals().GrantAsync(RunId, "implement", "dba@example.com", "Migration reviewed", _ct);
        var afterImplement = await Engine().ResumeAsync(RunId, Workflow(executor), _ct);

        Assert.Single(executor.Calls, c => c.NodeId == "implement");
        Assert.Equal(NodeStatus.Succeeded, afterImplement.Nodes["implement"].Status);
        Assert.Equal(Artifact.Create("implement", migration).Hash, afterImplement.Nodes["implement"].OutputHash);
        Assert.Equal(NodeStatus.AwaitingApproval, afterImplement.Nodes["merge"].Status);
    }

    [Fact]
    public async Task Policy_block_fails_the_gate_and_is_retried_with_feedback()
    {
        var leaking = Diffs.File("src/appsettings.json", "\"Db\": \"Password=hunter22\"");
        var executor = new FakeExecutor { Outputs = { ["implement"] = leaking } };

        var state = await Engine().RunAsync(RunId, "cr", Workflow(executor, new RetryPolicy(2, TimeSpan.FromMilliseconds(1))), _ct);

        Assert.Equal(NodeStatus.Failed, state.Nodes["implement"].Status);
        Assert.Equal(2, executor.Calls.Count(c => c.NodeId == "implement"));
        Assert.Contains("secrets", executor.Calls.Last(c => c.NodeId == "implement").Feedback, StringComparison.Ordinal);
        Assert.Contains(await EventsAsync(), e => e.Type == RunEventType.PolicyViolation && e.Data["outcome"] == "Block");
        Assert.DoesNotContain("hunter22", string.Concat((await EventsAsync()).Select(e => e.Message)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Approval_for_node_not_awaiting_is_rejected()
    {
        await Engine().RunAsync(RunId, "cr", Workflow(new FakeExecutor()), _ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Approvals().GrantAsync(RunId, "design", "x@example.com", null, _ct));
    }

    [Fact]
    public async Task Reject_requires_a_reason()
    {
        await Engine().RunAsync(RunId, "cr", Workflow(new FakeExecutor()), _ct);

        await Assert.ThrowsAsync<ArgumentException>(() => Approvals().RejectAsync(RunId, "merge", "x@example.com", " ", _ct));
    }
}

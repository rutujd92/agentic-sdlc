using Orchestrator.Core.Events;
using Orchestrator.Core.Metrics;

namespace Orchestrator.Tests.Metrics;

public class RunMetricsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
    private long _sequence;

    private RunEvent E(int second, RunEventType type, string? node = null, string actor = "system", params (string K, string V)[] data) =>
        new(++_sequence, "r1", type, T0.AddSeconds(second), actor) { NodeId = node, Data = data.ToDictionary(d => d.K, d => d.V) };

    // a: fails once, retried after 2s, succeeds at 5s. b: succeeds. merge: waits 60s for a human, then runs.
    private List<RunEvent> SampleRun() =>
    [
        E(0, RunEventType.RunStarted, data: [("nodes", "a,b,merge"), ("edges", "a->b;b->merge")]),
        E(1, RunEventType.NodeStarted, "a"),
        E(2, RunEventType.GateFailed, "a", data: [("gate", "policy"), ("phase", "exit")]),
        E(2, RunEventType.PolicyViolation, "a", data: [("gate", "policy"), ("outcome", "Block")]),
        E(2, RunEventType.RetryScheduled, "a"),
        E(4, RunEventType.NodeStarted, "a"),
        E(5, RunEventType.NodeSucceeded, "a", "agent:implement", [("outputHash", "h"), ("model", "m"), ("inputTokens", "1000"), ("outputTokens", "200")]),
        E(5, RunEventType.NodeStarted, "b"),
        E(8, RunEventType.NodeSucceeded, "b", "agent:tests", [("outputHash", "h2"), ("model", "m"), ("inputTokens", "500"), ("outputTokens", "100")]),
        E(8, RunEventType.ApprovalRequested, "merge", data: [("phase", "pre")]),
        E(8, RunEventType.RunPaused),
        E(68, RunEventType.ApprovalGranted, "merge", "human:lead@example.com", [("phase", "pre")]),
        E(70, RunEventType.RunResumed),
        E(70, RunEventType.NodeStarted, "merge"),
        E(71, RunEventType.NodeSucceeded, "merge", "tool:git", [("outputHash", "h3")]),
        E(71, RunEventType.RunCompleted),
    ];

    [Fact]
    public void Computes_latency_human_wait_and_active_time()
    {
        var m = RunMetrics.From(SampleRun());

        Assert.Equal("Succeeded", m.Status);
        Assert.Equal(TimeSpan.FromSeconds(71), m.EndToEndLatency);
        Assert.Equal(TimeSpan.FromSeconds(60), m.HumanWaitTime);
        Assert.Equal(TimeSpan.FromSeconds(11), m.ActiveTime);
    }

    [Fact]
    public void Counts_attempts_retries_policy_and_approvals()
    {
        var m = RunMetrics.From(SampleRun());

        Assert.Equal(4, m.Attempts);
        Assert.Equal(1, m.Retries);
        Assert.Equal(0.25, m.RetryRate);
        Assert.Equal(1, m.PolicyViolations);
        Assert.Equal(1, m.ApprovalsRequested);
        Assert.Equal(1, m.ApprovalsGranted);
        Assert.Equal(0, m.Rollbacks);
    }

    [Fact]
    public void Mttr_measures_first_failure_to_recovery()
    {
        var m = RunMetrics.From(SampleRun());

        Assert.Equal(TimeSpan.FromSeconds(3), m.MeanTimeToRecovery);
        Assert.Equal(1, m.Recoveries);
    }

    [Fact]
    public void Per_node_latency_and_token_totals()
    {
        var m = RunMetrics.From(SampleRun());

        Assert.Equal(TimeSpan.FromSeconds(4), m.Nodes["a"].Latency);
        Assert.Equal(2, m.Nodes["a"].Attempts);
        Assert.Equal(1500, m.InputTokens);
        Assert.Equal(300, m.OutputTokens);
        Assert.Equal("agent:implement", m.Nodes["a"].Actor);
    }

    [Fact]
    public void Aggregate_reports_success_rate_and_rollback_frequency()
    {
        var ok = RunMetrics.From(SampleRun());
        _sequence = 0;
        var rolledBack = RunMetrics.From(
        [
            E(0, RunEventType.RunStarted, data: [("nodes", "a")]),
            E(1, RunEventType.NodeStarted, "a"),
            E(2, RunEventType.NodeFailed, "a"),
            E(2, RunEventType.RunFailed),
            E(3, RunEventType.RolledBack, data: [("checkpointId", "c")]),
        ]);
        var paused = RunMetrics.From([E(0, RunEventType.RunStarted, data: [("nodes", "a")]), E(1, RunEventType.RunPaused)]);

        var total = AggregateMetrics.From([ok, rolledBack, paused]);

        Assert.Equal(3, total.Runs);
        Assert.Equal(2, total.FinishedRuns);
        Assert.Equal(0.5, total.SuccessRate);
        Assert.Equal(0.5, total.RollbackFrequency);
        Assert.Equal(TimeSpan.FromSeconds(3), total.MeanTimeToRecovery);
    }
}

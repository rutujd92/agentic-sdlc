using System.Globalization;
using Orchestrator.Core.Events;
using Orchestrator.Core.State;

namespace Orchestrator.Core.Metrics;

public sealed record NodeMetrics(string NodeId, int Attempts, TimeSpan? Latency, string? Actor, long InputTokens, long OutputTokens, string? Model);

/// <summary>
/// Reliability metrics for one run, computed purely from its event log (no separate instrumentation to drift).
/// Human wait (paused/stopped until a human acts) is separated from active execution time.
/// MTTR = time from a node's first failure signal (failed gate, failed attempt, scheduled retry) to its recovery.
/// </summary>
public sealed record RunMetrics(
    string RunId,
    string Status,
    TimeSpan EndToEndLatency,
    TimeSpan HumanWaitTime,
    int Attempts,
    int Retries,
    int Fallbacks,
    int Rollbacks,
    int PolicyViolations,
    int ApprovalsRequested,
    int ApprovalsGranted,
    int ApprovalsRejected,
    int Replans,
    int Invalidations,
    int Recoveries,
    TimeSpan? MeanTimeToRecovery,
    long InputTokens,
    long OutputTokens,
    IReadOnlyDictionary<string, NodeMetrics> Nodes)
{
    public TimeSpan ActiveTime => EndToEndLatency - HumanWaitTime;

    public double RetryRate => Attempts == 0 ? 0 : (double)Retries / Attempts;

    public static RunMetrics From(IReadOnlyList<RunEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0)
        {
            throw new ArgumentException("A run needs at least one event.", nameof(events));
        }

        int Count(RunEventType type) => events.Count(e => e.Type == type);
        var recoveries = RecoveryTimes(events);
        var nodes = events.Where(e => e.NodeId is not null).GroupBy(e => e.NodeId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => NodeFrom(g.Key, g.ToList()), StringComparer.Ordinal);

        return new RunMetrics(
            events[0].RunId,
            RunState.Rebuild(events).Status.ToString(),
            events[^1].Timestamp - events[0].Timestamp,
            HumanWait(events),
            Count(RunEventType.NodeStarted),
            Count(RunEventType.RetryScheduled),
            Count(RunEventType.FallbackUsed),
            Count(RunEventType.RolledBack),
            Count(RunEventType.PolicyViolation),
            Count(RunEventType.ApprovalRequested),
            Count(RunEventType.ApprovalGranted),
            Count(RunEventType.ApprovalRejected),
            Count(RunEventType.Replanned),
            Count(RunEventType.NodeInvalidated),
            recoveries.Count,
            recoveries.Count == 0 ? null : TimeSpan.FromTicks((long)recoveries.Average(r => r.Ticks)),
            nodes.Values.Sum(n => n.InputTokens),
            nodes.Values.Sum(n => n.OutputTokens),
            nodes);
    }

    private static TimeSpan HumanWait(IReadOnlyList<RunEvent> events)
    {
        var total = TimeSpan.Zero;
        DateTimeOffset? waitingSince = null;
        foreach (var e in events)
        {
            if (e.Type is RunEventType.RunPaused or RunEventType.SafeStopped)
            {
                waitingSince ??= e.Timestamp;
            }
            else if (waitingSince is { } since && (e.Actor.StartsWith("human:", StringComparison.Ordinal) || e.Type == RunEventType.RunResumed))
            {
                total += e.Timestamp - since;
                waitingSince = null;
            }
        }

        return waitingSince is { } open ? total + (events[^1].Timestamp - open) : total;
    }

    private static List<TimeSpan> RecoveryTimes(IReadOnlyList<RunEvent> events)
    {
        var openSince = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var recoveries = new List<TimeSpan>();
        foreach (var e in events.Where(e => e.NodeId is not null))
        {
            if (e.Type is RunEventType.GateFailed or RunEventType.NodeFailed or RunEventType.RetryScheduled)
            {
                openSince.TryAdd(e.NodeId!, e.Timestamp);
            }
            else if (e.Type == RunEventType.NodeSucceeded && openSince.Remove(e.NodeId!, out var since))
            {
                recoveries.Add(e.Timestamp - since);
            }
        }

        return recoveries;
    }

    private static NodeMetrics NodeFrom(string nodeId, List<RunEvent> events)
    {
        var started = events.FirstOrDefault(e => e.Type == RunEventType.NodeStarted)?.Timestamp;
        var ended = events.LastOrDefault(e => e.Type is RunEventType.NodeSucceeded or RunEventType.NodeFailed)?.Timestamp;
        var succeeded = events.LastOrDefault(e => e.Type == RunEventType.NodeSucceeded);
        long Tokens(string key) => events.Where(e => e.Type == RunEventType.NodeSucceeded)
            .Sum(e => e.Data.TryGetValue(key, out var v) ? long.Parse(v, CultureInfo.InvariantCulture) : 0);

        return new NodeMetrics(
            nodeId,
            events.Count(e => e.Type == RunEventType.NodeStarted),
            started is { } s && ended is { } f ? f - s : null,
            succeeded?.Actor,
            Tokens("inputTokens"),
            Tokens("outputTokens"),
            succeeded?.Data.GetValueOrDefault("model"));
    }
}

/// <summary>Reliability metrics across runs (the CLI <c>metrics</c> command).</summary>
public sealed record AggregateMetrics(
    int Runs,
    int FinishedRuns,
    double SuccessRate,
    double RollbackFrequency,
    double RetryRate,
    TimeSpan? MeanTimeToRecovery,
    TimeSpan? MeanEndToEndLatency,
    TimeSpan TotalHumanWait,
    long InputTokens,
    long OutputTokens)
{
    private static readonly string[] Finished = ["Succeeded", "Failed", "RolledBack"];

    public static AggregateMetrics From(IReadOnlyList<RunMetrics> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        var finished = runs.Where(r => Finished.Contains(r.Status)).ToArray();
        var recoveries = runs.Where(r => r.MeanTimeToRecovery is not null).ToArray();
        var recoveryCount = recoveries.Sum(r => r.Recoveries);
        var attempts = runs.Sum(r => r.Attempts);

        return new AggregateMetrics(
            runs.Count,
            finished.Length,
            finished.Length == 0 ? 0 : (double)finished.Count(r => r.Status == "Succeeded") / finished.Length,
            finished.Length == 0 ? 0 : (double)finished.Count(r => r.Rollbacks > 0) / finished.Length,
            attempts == 0 ? 0 : (double)runs.Sum(r => r.Retries) / attempts,
            recoveryCount == 0 ? null : TimeSpan.FromTicks(recoveries.Sum(r => r.MeanTimeToRecovery!.Value.Ticks * r.Recoveries) / recoveryCount),
            finished.Length == 0 ? null : TimeSpan.FromTicks((long)finished.Average(r => r.EndToEndLatency.Ticks)),
            TimeSpan.FromTicks(runs.Sum(r => r.HumanWaitTime.Ticks)),
            runs.Sum(r => r.InputTokens),
            runs.Sum(r => r.OutputTokens));
    }
}

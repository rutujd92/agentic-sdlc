namespace Orchestrator.Core.Events;

/// <summary>
/// One immutable entry in a run's append-only log. The log is the audit trail, the source for
/// rebuilding <see cref="State.RunState"/> (resume) and the input for reliability metrics.
/// </summary>
/// <param name="Actor">Who caused the event: <c>system</c>, <c>agent:&lt;name&gt;</c> or <c>human:&lt;email&gt;</c>.</param>
public sealed record RunEvent(long Sequence, string RunId, RunEventType Type, DateTimeOffset Timestamp, string Actor)
{
    public string? NodeId { get; init; }

    /// <summary>Human-readable message or decision rationale.</summary>
    public string? Message { get; init; }

    public IReadOnlyDictionary<string, string> Data { get; init; } = new Dictionary<string, string>();
}

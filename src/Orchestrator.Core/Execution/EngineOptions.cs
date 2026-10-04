using Orchestrator.Core.Events;

namespace Orchestrator.Core.Execution;

public sealed class EngineOptions
{
    public int MaxParallelism { get; init; } = 4;

    /// <summary>Safe-stop once this many node attempts have been made in the run (runaway-loop guard).</summary>
    public int MaxTotalAttempts { get; init; } = 100;

    /// <summary>Safe-stop once the run has been executing longer than this.</summary>
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromHours(1);

    /// <summary>Called after each event is persisted, serialized (never concurrently). Used for live CLI output.</summary>
    public Action<RunEvent>? OnEvent { get; init; }
}

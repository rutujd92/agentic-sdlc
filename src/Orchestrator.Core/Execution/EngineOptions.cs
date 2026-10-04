using Orchestrator.Core.Events;

namespace Orchestrator.Core.Execution;

public sealed class EngineOptions
{
    public int MaxParallelism { get; init; } = 4;

    /// <summary>Called after each event is persisted, serialized (never concurrently). Used for live CLI output.</summary>
    public Action<RunEvent>? OnEvent { get; init; }
}

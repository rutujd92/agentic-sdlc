namespace Orchestrator.Core.Execution;

/// <summary>Operator kill switch, checked at node boundaries so a stop never interrupts a node mid-write.</summary>
public interface IStopSignal
{
    bool IsStopRequested(string runId);
}

public sealed class NeverStop : IStopSignal
{
    public bool IsStopRequested(string runId) => false;
}

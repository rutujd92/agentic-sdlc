using Orchestrator.Core.Execution;

namespace Orchestrator.Tests.Fakes;

public sealed class FakeCheckpointStore : ICheckpointStore
{
    private int _next;

    public List<string> Created { get; } = [];

    public List<string> RolledBackTo { get; } = [];

    public Task<string?> CreateAsync(string runId, string label, CancellationToken cancellationToken)
    {
        lock (Created)
        {
            var id = $"cp{++_next}:{label}";
            Created.Add(id);
            return Task.FromResult<string?>(id);
        }
    }

    public Task RollbackAsync(string runId, string checkpointId, CancellationToken cancellationToken)
    {
        RolledBackTo.Add(checkpointId);
        return Task.CompletedTask;
    }
}

public sealed class ManualStopSignal : IStopSignal
{
    private volatile bool _requested;

    public void Request() => _requested = true;

    public void Reset() => _requested = false;

    public bool IsStopRequested(string runId) => _requested;
}

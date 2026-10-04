namespace Orchestrator.Core.Events;

public interface IEventStore
{
    Task AppendAsync(RunEvent runEvent, CancellationToken cancellationToken);

    Task<IReadOnlyList<RunEvent>> ReadAsync(string runId, CancellationToken cancellationToken);
}

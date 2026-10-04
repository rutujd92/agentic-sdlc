using System.Collections.Concurrent;
using Orchestrator.Core.Events;
using Orchestrator.Core.Execution;

namespace Orchestrator.Tests.Fakes;

public sealed class InMemoryEventStore : IEventStore
{
    private readonly ConcurrentDictionary<string, List<RunEvent>> _runs = new(StringComparer.Ordinal);

    public Task AppendAsync(RunEvent runEvent, CancellationToken cancellationToken)
    {
        var events = _runs.GetOrAdd(runEvent.RunId, _ => []);
        lock (events)
        {
            events.Add(runEvent);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RunEvent>> ReadAsync(string runId, CancellationToken cancellationToken)
    {
        var events = _runs.GetOrAdd(runId, _ => []);
        lock (events)
        {
            return Task.FromResult<IReadOnlyList<RunEvent>>(events.ToArray());
        }
    }
}

public sealed class InMemoryArtifactStore : IArtifactStore
{
    private readonly ConcurrentDictionary<(string RunId, string NodeId), Artifact> _artifacts = new();

    public Task SaveAsync(string runId, Artifact artifact, CancellationToken cancellationToken)
    {
        _artifacts[(runId, artifact.NodeId)] = artifact;
        return Task.CompletedTask;
    }

    public Task<Artifact?> GetAsync(string runId, string nodeId, CancellationToken cancellationToken) =>
        Task.FromResult(_artifacts.GetValueOrDefault((runId, nodeId)));
}

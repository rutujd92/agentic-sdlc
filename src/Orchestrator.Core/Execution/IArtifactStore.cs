namespace Orchestrator.Core.Execution;

public interface IArtifactStore
{
    Task SaveAsync(string runId, Artifact artifact, CancellationToken cancellationToken);

    Task<Artifact?> GetAsync(string runId, string nodeId, CancellationToken cancellationToken);
}

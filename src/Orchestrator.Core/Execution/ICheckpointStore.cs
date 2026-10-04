namespace Orchestrator.Core.Execution;

/// <summary>Snapshots the workspace after each successful node so an unrecoverable failure can roll back.</summary>
public interface ICheckpointStore
{
    /// <returns>Checkpoint id, or null when checkpoints are disabled.</returns>
    Task<string?> CreateAsync(string runId, string label, CancellationToken cancellationToken);

    Task RollbackAsync(string runId, string checkpointId, CancellationToken cancellationToken);
}

public sealed class NoCheckpoints : ICheckpointStore
{
    public Task<string?> CreateAsync(string runId, string label, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

    public Task RollbackAsync(string runId, string checkpointId, CancellationToken cancellationToken) => Task.CompletedTask;
}

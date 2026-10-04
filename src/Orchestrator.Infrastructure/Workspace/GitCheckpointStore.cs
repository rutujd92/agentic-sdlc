using Orchestrator.Core.Execution;

namespace Orchestrator.Infrastructure.Workspace;

/// <summary>
/// Checkpoints are commits in the run's git worktree; rollback is <c>reset --hard</c> plus <c>clean -fd</c>,
/// which also removes untracked files a failed agent left behind. Must only ever point at an agent worktree.
/// </summary>
public sealed class GitCheckpointStore(string workingDirectory) : ICheckpointStore
{
    public async Task<string?> CreateAsync(string runId, string label, CancellationToken cancellationToken)
    {
        await GitAsync(cancellationToken, "add", "-A");
        await GitAsync(cancellationToken,
            "-c", "user.name=orchestrator", "-c", "user.email=orchestrator@localhost",
            "commit", "--allow-empty", "-q", "-m", $"checkpoint({runId}): {label}");
        return (await GitAsync(cancellationToken, "rev-parse", "HEAD")).Trim();
    }

    public async Task RollbackAsync(string runId, string checkpointId, CancellationToken cancellationToken)
    {
        await GitAsync(cancellationToken, "reset", "--hard", "-q", checkpointId);
        await GitAsync(cancellationToken, "clean", "-fdq");
    }

    private async Task<string> GitAsync(CancellationToken cancellationToken, params string[] arguments)
    {
        var result = await ProcessRunner.RunAsync("git", workingDirectory, arguments, cancellationToken);
        return result.Succeeded
            ? result.StandardOutput
            : throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {result.StandardError.Trim()}");
    }
}

using Orchestrator.Infrastructure.Workspace;

namespace Orchestrator.Tests.Agents;

public sealed class GitWorktreeCheckpointTests : IDisposable
{
    private readonly TempGitRepo _repo = new();
    private readonly CancellationToken _ct = CancellationToken.None;

    [Fact]
    public async Task Rollback_restores_files_and_removes_untracked_output()
    {
        var worktree = await GitWorktree.OpenAsync(_repo.Root, _repo.RunsRoot, "run-1", _ct);
        await worktree.ApplyFilesAsync([new FileChange("src/UrlShortener.Api/Program.cs", "v2")], _ct);
        var checkpoint = await worktree.CreateAsync("run-1", "after design", _ct);

        await worktree.ApplyFilesAsync([new FileChange("src/UrlShortener.Api/Program.cs", "broken"), new FileChange("src/UrlShortener.Api/Junk.cs", "partial")], _ct);
        await worktree.RollbackAsync("run-1", checkpoint!, _ct);

        Assert.Equal("v2", File.ReadAllText(Path.Combine(worktree.Path, "src", "UrlShortener.Api", "Program.cs")));
        Assert.False(File.Exists(Path.Combine(worktree.Path, "src", "UrlShortener.Api", "Junk.cs")));
        Assert.Matches("^[0-9a-f]{40}$", checkpoint);
        Assert.Contains("checkpoint(run-1): after design", _repo.Git("-C", worktree.Path, "log", "-1", "--format=%s"), StringComparison.Ordinal);
    }

    public void Dispose() => _repo.Dispose();
}

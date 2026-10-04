using Orchestrator.Infrastructure.Workspace;

namespace Orchestrator.Tests.Agents;

public sealed class GitWorktreeTests : IDisposable
{
    private readonly TempGitRepo _repo = new();
    private readonly CancellationToken _ct = CancellationToken.None;

    [Fact]
    public async Task Creates_worktree_on_run_branch_and_reopens_it()
    {
        var worktree = await GitWorktree.OpenAsync(_repo.Root, _repo.RunsRoot, "run-1", _ct);
        var again = await GitWorktree.OpenAsync(_repo.Root, _repo.RunsRoot, "run-1", _ct);

        Assert.Equal("orch/run-1", worktree.Branch);
        Assert.Equal(worktree.Path, again.Path);
        Assert.True(File.Exists(Path.Combine(worktree.Path, "src", "UrlShortener.Api", "Program.cs")));
        Assert.Contains("orch/run-1", _repo.Git("branch", "--list", "orch/*"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplyFiles_writes_files_and_returns_unified_diff_of_only_those_files()
    {
        var worktree = await GitWorktree.OpenAsync(_repo.Root, _repo.RunsRoot, "run-1", _ct);

        var diff = await worktree.ApplyFilesAsync(
            [new FileChange("src/UrlShortener.Api/Program.cs", "var app = 2;\n"), new FileChange("src/UrlShortener.Api/New.cs", "class New {}\n")], _ct);

        Assert.Contains("diff --git a/src/UrlShortener.Api/Program.cs b/src/UrlShortener.Api/Program.cs", diff, StringComparison.Ordinal);
        Assert.Contains("+var app = 2;", diff, StringComparison.Ordinal);
        Assert.Contains("diff --git a/src/UrlShortener.Api/New.cs b/src/UrlShortener.Api/New.cs", diff, StringComparison.Ordinal);
        Assert.Contains("+class New {}", diff, StringComparison.Ordinal);
        Assert.Equal("var app = 1;\n", File.ReadAllText(Path.Combine(_repo.Root, "src", "UrlShortener.Api", "Program.cs")));
    }

    [Theory]
    [InlineData("../escape.cs")]
    [InlineData("/etc/passwd")]
    [InlineData("src/../../escape.cs")]
    [InlineData(".git/config")]
    public async Task ApplyFiles_rejects_paths_outside_the_worktree(string path)
    {
        var worktree = await GitWorktree.OpenAsync(_repo.Root, _repo.RunsRoot, "run-1", _ct);

        await Assert.ThrowsAsync<ArgumentException>(() => worktree.ApplyFilesAsync([new FileChange(path, "x")], _ct));
    }

    [Fact]
    public async Task Concurrent_applies_and_checkpoints_do_not_collide()
    {
        var worktree = await GitWorktree.OpenAsync(_repo.Root, _repo.RunsRoot, "run-1", _ct);

        await Task.WhenAll(Enumerable.Range(0, 6).Select(async i =>
        {
            await worktree.ApplyFilesAsync([new FileChange($"src/UrlShortener.Api/F{i}.cs", $"class F{i} {{}}\n")], _ct);
            await worktree.CreateAsync("run-1", $"after f{i}", _ct);
        }));

        Assert.Equal(string.Empty, _repo.Git("-C", worktree.Path, "status", "--porcelain").Trim());
        Assert.Equal(6, Directory.GetFiles(Path.Combine(worktree.Path, "src", "UrlShortener.Api"), "F*.cs").Length);
    }

    [Fact]
    public async Task Context_includes_tracked_source_and_conventions_but_not_runs()
    {
        var worktree = await GitWorktree.OpenAsync(_repo.Root, _repo.RunsRoot, "run-1", _ct);

        var context = await worktree.ReadContextAsync(_ct);

        Assert.Contains("=== src/UrlShortener.Api/Program.cs ===", context, StringComparison.Ordinal);
        Assert.Contains("=== CLAUDE.md ===", context, StringComparison.Ordinal);
        Assert.DoesNotContain("runs/", context, StringComparison.Ordinal);
    }

    public void Dispose() => _repo.Dispose();
}

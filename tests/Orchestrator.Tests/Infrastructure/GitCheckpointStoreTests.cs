using System.Diagnostics;
using Orchestrator.Infrastructure.Workspace;

namespace Orchestrator.Tests.Infrastructure;

public sealed class GitCheckpointStoreTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), $"orch-git-{Guid.NewGuid():N}");
    private readonly CancellationToken _ct = CancellationToken.None;

    public GitCheckpointStoreTests()
    {
        Directory.CreateDirectory(_repo);
        Git("init", "-q");
        File.WriteAllText(Path.Combine(_repo, "app.txt"), "v1");
        Git("add", "-A");
        Git("-c", "user.name=t", "-c", "user.email=t@localhost", "commit", "-q", "-m", "init");
    }

    [Fact]
    public async Task Rollback_restores_files_and_removes_untracked_changes()
    {
        var store = new GitCheckpointStore(_repo);
        File.WriteAllText(Path.Combine(_repo, "app.txt"), "v2");
        var checkpoint = await store.CreateAsync("run-1", "after design", _ct);

        File.WriteAllText(Path.Combine(_repo, "app.txt"), "broken");
        File.WriteAllText(Path.Combine(_repo, "junk.txt"), "partial output");
        await store.RollbackAsync("run-1", checkpoint!, _ct);

        Assert.Equal("v2", File.ReadAllText(Path.Combine(_repo, "app.txt")));
        Assert.False(File.Exists(Path.Combine(_repo, "junk.txt")));
    }

    [Fact]
    public async Task Checkpoint_commit_message_records_run_and_label()
    {
        var checkpoint = await new GitCheckpointStore(_repo).CreateAsync("run-1", "after design", _ct);

        Assert.Matches("^[0-9a-f]{40}$", checkpoint);
        Assert.Contains("checkpoint(run-1): after design", Git("log", "-1", "--format=%s"), StringComparison.Ordinal);
    }

    public void Dispose() => Directory.Delete(_repo, recursive: true);

    private string Git(params string[] args)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = _repo, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
    }
}

using Orchestrator.Infrastructure.Workspace;

namespace Orchestrator.Tests.Agents;

/// <summary>A throwaway git repository with a tiny shortener-shaped layout.</summary>
public sealed class TempGitRepo : IDisposable
{
    public TempGitRepo()
    {
        Directory.CreateDirectory(Path.Combine(Root, "src", "UrlShortener.Api"));
        File.WriteAllText(Path.Combine(Root, "src", "UrlShortener.Api", "Program.cs"), "var app = 1;\n");
        File.WriteAllText(Path.Combine(Root, "CLAUDE.md"), "# Rules\n");
        File.WriteAllText(Path.Combine(Root, ".gitignore"), "runs/\n");
        Git("init", "-q", "-b", "main");
        Git("add", "-A");
        Git("-c", "user.name=t", "-c", "user.email=t@localhost", "commit", "-q", "-m", "init");
    }

    public string Root { get; } = Path.Combine(Path.GetTempPath(), $"orch-repo-{Guid.NewGuid():N}");

    public string RunsRoot => Path.Combine(Root, "runs");

    public string Git(params string[] args) =>
        ProcessRunner.RunAsync("git", Root, args, CancellationToken.None).GetAwaiter().GetResult().StandardOutput;

    public void Dispose()
    {
        ProcessRunner.RunAsync("git", Root, ["worktree", "prune"], CancellationToken.None).GetAwaiter().GetResult();
        Directory.Delete(Root, recursive: true);
    }
}

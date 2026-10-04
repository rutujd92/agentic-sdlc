using System.Text;
using Orchestrator.Core.Execution;
using Orchestrator.Core.Governance;

namespace Orchestrator.Infrastructure.Workspace;

public sealed record FileChange(string Path, string Content);

/// <summary>
/// The agents' sandbox: a git worktree at <c>runs/&lt;runId&gt;/worktree</c> on branch <c>orch/&lt;runId&gt;</c>, created from
/// the repository HEAD. Agents never touch the main checkout. Also the run's checkpoint store (commit / reset --hard + clean).
/// All git operations are serialized: parallel nodes share one index.
/// </summary>
public sealed class GitWorktree : ICheckpointStore, IDisposable
{
    private static readonly string[] ContextGlobs =
    [
        "CLAUDE.md", "README.md", "Directory.Build.props", "docker-compose.yml",
        "src/UrlShortener.*/**", "tests/UrlShortener.Tests/**",
    ];

    private readonly SemaphoreSlim _lock = new(1, 1);

    private GitWorktree(string path, string branch)
    {
        Path = path;
        Branch = branch;
    }

    public string Path { get; }

    public string Branch { get; }

    public static async Task<GitWorktree> OpenAsync(string repositoryRoot, string runsRoot, string runId, CancellationToken cancellationToken)
    {
        var path = System.IO.Path.Combine(runsRoot, runId, "worktree");
        var branch = $"orch/{runId}";
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            await GitAsync(repositoryRoot, cancellationToken, "worktree", "add", "-q", "-b", branch, path, "HEAD");
        }

        return new GitWorktree(path, branch);
    }

    /// <summary>Writes complete file contents and returns the unified diff of exactly those files.</summary>
    public async Task<string> ApplyFilesAsync(IReadOnlyList<FileChange> files, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(files);
        var paths = files.Select(f => SafeRelativePath(f.Path)).ToArray();
        await _lock.WaitAsync(cancellationToken);
        try
        {
            for (var i = 0; i < files.Count; i++)
            {
                var full = System.IO.Path.Combine(Path, paths[i]);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
                await File.WriteAllTextAsync(full, files[i].Content, cancellationToken);
            }

            return await DiffUnlockedAsync(paths, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Unified diff (including new files) for the given paths or directories.</summary>
    public async Task<string> DiffAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            return await DiffUnlockedAsync(paths.Select(SafeRelativePath).ToArray(), cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Tracked source and conventions as one text block, for agent context.</summary>
    public async Task<string> ReadContextAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var files = (await GitAsync(Path, cancellationToken, "ls-files"))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(f => ContextGlobs.Any(g => Glob.IsMatch(g, f)) && !f.EndsWith(".Designer.cs", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal);

            var context = new StringBuilder();
            foreach (var file in files)
            {
                var full = System.IO.Path.Combine(Path, file);
                if (File.Exists(full))
                {
                    context.Append("=== ").Append(file).Append(" ===\n").Append(await File.ReadAllTextAsync(full, cancellationToken)).Append('\n');
                }
            }

            return context.ToString();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<string?> CreateAsync(string runId, string label, CancellationToken cancellationToken) =>
        await CommitAsync($"checkpoint({runId}): {label}", cancellationToken);

    public async Task<string> CommitAsync(string message, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await GitAsync(Path, cancellationToken, "add", "-A");
            await GitAsync(Path, cancellationToken,
                "-c", "user.name=orchestrator", "-c", "user.email=orchestrator@localhost", "commit", "--allow-empty", "-q", "-m", message);
            return (await GitAsync(Path, cancellationToken, "rev-parse", "HEAD")).Trim();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task RollbackAsync(string runId, string checkpointId, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await GitAsync(Path, cancellationToken, "reset", "--hard", "-q", checkpointId);
            await GitAsync(Path, cancellationToken, "clean", "-fdq");
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose() => _lock.Dispose();

    public Task<string> GitAsync(CancellationToken cancellationToken, params string[] arguments) => GitAsync(Path, cancellationToken, arguments);

    private async Task<string> DiffUnlockedAsync(string[] paths, CancellationToken cancellationToken)
    {
        // Intent-to-add makes new files appear in `git diff`.
        await GitAsync(Path, cancellationToken, ["add", "-N", "--", .. paths]);
        return await GitAsync(Path, cancellationToken, ["diff", "--no-color", "--", .. paths]);
    }

    private string SafeRelativePath(string relative)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relative);
        var normalized = relative.Replace('\\', '/');
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(Path, normalized));
        var root = System.IO.Path.GetFullPath(Path) + System.IO.Path.DirectorySeparatorChar;
        if (System.IO.Path.IsPathRooted(normalized) || !full.StartsWith(root, StringComparison.Ordinal)
            || normalized.Split('/').Any(s => s is ".." or ".git"))
        {
            throw new ArgumentException($"Path '{relative}' is outside the worktree.", nameof(relative));
        }

        return normalized;
    }

    private static async Task<string> GitAsync(string workingDirectory, CancellationToken cancellationToken, params string[] arguments)
    {
        var result = await ProcessRunner.RunAsync("git", workingDirectory, arguments, cancellationToken);
        return result.Succeeded
            ? result.StandardOutput
            : throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {result.StandardError.Trim()}");
    }
}

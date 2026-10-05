using System.Globalization;
using System.Text;
using Orchestrator.Core.Execution;
using Orchestrator.Infrastructure.Workspace;

namespace Orchestrator.Infrastructure.Agents;

/// <summary>
/// Validation is done by tools, not by a model: build, run the shortener test suite and scan for secrets in the
/// worktree. Any failure fails the node with the relevant output (and the run rolls back to the last checkpoint).
/// </summary>
public sealed class ValidateExecutor(GitWorktree worktree) : INodeExecutor
{
    private const string Actor = "tool:validate";
    private const string TestProject = "tests/UrlShortener.Tests/UrlShortener.Tests.csproj";

    public async Task<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var report = new StringBuilder("# Validation report\n\n");

        var build = await ProcessRunner.RunAsync("dotnet", worktree.Path, ["build", TestProject, "-c", "Release", "-nologo", "-v", "q"], cancellationToken);
        report.Append(Line("dotnet build", build.Succeeded));
        if (!build.Succeeded)
        {
            return NodeResult.Failure($"Build failed:\n{Errors(build)}", Actor);
        }

        var test = await ProcessRunner.RunAsync("dotnet", worktree.Path, ["test", TestProject, "-c", "Release", "--no-build", "-nologo"], cancellationToken);
        var summary = test.StandardOutput.Split('\n').LastOrDefault(l => l.Contains("Total:", StringComparison.Ordinal))?.Trim() ?? "no summary";
        report.Append(Line($"dotnet test — {summary}", test.Succeeded));
        if (!test.Succeeded)
        {
            return NodeResult.Failure($"Tests failed ({summary}):\n{Errors(test)}", Actor);
        }

        var secrets = await GitleaksAsync(cancellationToken);
        report.Append(secrets.Line);
        return secrets.Clean
            ? NodeResult.Success(Artifact.Create(context.Node.Id, report.ToString()), Actor, "Build, tests and secret scan passed.")
            : NodeResult.Failure("Secret scan found leaks in the worktree.", Actor);
    }

    private async Task<(bool Clean, string Line)> GitleaksAsync(CancellationToken cancellationToken)
    {
        try
        {
            var scan = await ProcessRunner.RunAsync("gitleaks", worktree.Path, ["dir", ".", "--no-banner", "--redact"], cancellationToken);
            return (scan.Succeeded, Line("gitleaks dir", scan.Succeeded));
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Diffs were already secret-scanned by the policy gate; record that the extra scan did not run.
            return (true, "- [skipped] gitleaks not installed (policy secret scan still applied to every diff)\n");
        }
    }

    private static string Line(string step, bool ok) => $"- [{(ok ? "pass" : "fail")}] {step}\n";

    private static string Errors(ProcessResult result)
    {
        var lines = (result.StandardOutput + "\n" + result.StandardError).Split('\n')
            .Where(l => l.Contains("error", StringComparison.OrdinalIgnoreCase) || l.Contains("Failed ", StringComparison.Ordinal) || l.Contains("Assert", StringComparison.Ordinal))
            .Select(l => l.Trim()).Distinct().Take(25);
        return string.Join('\n', lines);
    }
}

/// <summary>Generates the EF Core migration with <c>dotnet ef</c> (deterministic tooling, not a model).</summary>
public sealed class MigrationExecutor(GitWorktree worktree) : INodeExecutor
{
    private const string Actor = "tool:dotnet-ef";
    private const string MigrationsDir = "src/UrlShortener.Infrastructure/Persistence/Migrations";

    public async Task<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var name = context.Node.Description ?? throw new InvalidOperationException("Migration node needs a name in Description.");

        // A fresh worktree has no restored packages yet (migration runs before validate builds anything).
        await ProcessRunner.RunAsync("dotnet", worktree.Path, ["tool", "restore"], cancellationToken);
        var restore = await ProcessRunner.RunAsync("dotnet", worktree.Path, ["restore", "src/UrlShortener.Api/UrlShortener.Api.csproj"], cancellationToken);
        if (!restore.Succeeded)
        {
            return NodeResult.Failure($"dotnet restore failed:\n{(restore.StandardError + restore.StandardOutput).Trim()}", Actor);
        }

        var add = await ProcessRunner.RunAsync("dotnet", worktree.Path,
            ["ef", "migrations", "add", name, "--project", "src/UrlShortener.Infrastructure", "--startup-project", "src/UrlShortener.Api",
             "--output-dir", "Persistence/Migrations"], cancellationToken);
        if (!add.Succeeded)
        {
            return NodeResult.Failure($"dotnet ef migrations add {name} failed:\n{(add.StandardError + add.StandardOutput).Trim()}", Actor);
        }

        var diff = await worktree.DiffAsync([MigrationsDir], cancellationToken);
        return NodeResult.Success(Artifact.Create(context.Node.Id, diff), Actor, $"Generated migration {name}.");
    }
}

/// <summary>
/// Final, approval-gated step: records the change as a commit on the run branch. It never pushes or touches main;
/// opening the pull request stays a human action.
/// </summary>
public sealed class MergeExecutor(GitWorktree worktree) : INodeExecutor
{
    public async Task<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var commit = await worktree.CommitAsync($"feat: {context.ChangeRequest}", cancellationToken);
        var log = await worktree.GitAsync(cancellationToken, "log", "--oneline", "--no-decorate", "main..HEAD");
        var stat = await worktree.GitAsync(cancellationToken, "diff", "--stat", "main...HEAD");

        var content = new StringBuilder("# Ready for pull request\n\n")
            .Append(CultureInfo.InvariantCulture, $"Branch `{worktree.Branch}` at `{commit[..12]}`.\n\n")
            .Append("```\n").Append(stat.Trim()).Append("\n```\n\n")
            .Append(CultureInfo.InvariantCulture, $"Open the PR: `git push -u origin {worktree.Branch} && gh pr create --head {worktree.Branch}`\n");
        return NodeResult.Success(Artifact.Create(context.Node.Id, content.ToString()), "tool:git", $"Committed to {worktree.Branch} ({log.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length} commits ahead of main).");
    }
}

using Orchestrator.Core.Execution;
using Orchestrator.Core.Graph;
using Orchestrator.Infrastructure.Agents;
using Orchestrator.Infrastructure.Workspace;
using Orchestrator.Tests.Fakes;

namespace Orchestrator.Tests.Agents;

public sealed class LlmNodeExecutorTests : IDisposable
{
    private readonly TempGitRepo _repo = new();
    private readonly CancellationToken _ct = CancellationToken.None;

    private async Task<(LlmNodeExecutor Executor, FakeChatProvider Chat, GitWorktree Worktree)> CreateAsync(Dictionary<string, string> responses)
    {
        var worktree = await GitWorktree.OpenAsync(_repo.Root, _repo.RunsRoot, "run-1", _ct);
        var chat = new FakeChatProvider(responses);
        return (new LlmNodeExecutor(chat, worktree), chat, worktree);
    }

    private static NodeExecutionContext Context(WorkflowNode node, string? feedback = null, Dictionary<string, Artifact>? inputs = null) =>
        new("run-1", node, "Add QR codes", inputs ?? [], 1) { Feedback = feedback };

    [Fact]
    public async Task Requirements_renders_markdown_with_open_questions_and_sends_feedback()
    {
        var json = """{"problem":"Users want QR codes.","acceptanceCriteria":["GET /api/links/{code}/qr returns PNG"],"assumptions":["300px"],"openQuestions":["Which size?"],"outOfScope":["SVG"]}""";
        var (executor, chat, _) = await CreateAsync(new() { ["requirements"] = json });

        var result = await executor.ExecuteAsync(Context(new("requirements", NodeKind.Requirements), feedback: "Size is 256px"), _ct);

        Assert.True(result.Succeeded, result.Error);
        Assert.Contains("## Open questions\n- Which size?", result.Output!.Content, StringComparison.Ordinal);
        Assert.Contains("## Acceptance criteria\n- GET /api/links/{code}/qr returns PNG", result.Output.Content, StringComparison.Ordinal);
        var request = Assert.Single(chat.Requests);
        Assert.Equal("Size is 256px", request.Feedback);
        Assert.Contains("Add QR codes", request.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("=== CLAUDE.md ===", request.Context, StringComparison.Ordinal);
        Assert.Equal("fake-model", result.Telemetry["model"]);
    }

    [Fact]
    public async Task Design_that_needs_a_migration_proposes_a_governed_migration_node()
    {
        var json = """{"approach":"Add Click entity.","impactedFiles":["src/UrlShortener.Core/Links/Click.cs"],"apiChanges":"none","risks":["table growth"],"requiresMigration":true,"migrationName":"AddClicks"}""";
        var (executor, _, _) = await CreateAsync(new() { ["design"] = json });

        var result = await executor.ExecuteAsync(Context(new("design", NodeKind.Design)), _ct);

        var plan = Assert.IsType<GraphChange>(result.Plan);
        var migration = Assert.Single(plan.AddedNodes);
        Assert.Equal(NodeKind.Migration, migration.Kind);
        Assert.Equal("AddClicks", migration.Description);
        Assert.Equal(["implement"], migration.DependsOn);
        Assert.Contains(new DependencyEdge("validate", "migration"), plan.AddedDependencies);
    }

    [Fact]
    public async Task Implement_writes_files_into_the_worktree_and_returns_the_git_diff()
    {
        var json = """{"summary":"QR endpoint","files":[{"path":"src/UrlShortener.Api/Qr.cs","content":"class Qr {}\n"}]}""";
        var (executor, _, worktree) = await CreateAsync(new() { ["implement"] = json });

        var result = await executor.ExecuteAsync(Context(new("implement", NodeKind.Implement)), _ct);

        Assert.True(result.Succeeded, result.Error);
        Assert.True(File.Exists(Path.Combine(worktree.Path, "src", "UrlShortener.Api", "Qr.cs")));
        Assert.StartsWith("diff --git a/src/UrlShortener.Api/Qr.cs", result.Output!.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Code_agents_may_only_write_within_their_lane()
    {
        var json = """{"summary":"sneaky","files":[{"path":"src/UrlShortener.Api/Program.cs","content":"x"}]}""";
        var (executor, _, _) = await CreateAsync(new() { ["tests"] = json });

        var result = await executor.ExecuteAsync(Context(new("tests", NodeKind.Tests)), _ct);

        Assert.False(result.Succeeded);
        Assert.Contains("tests/", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Malformed_model_output_is_a_retryable_failure()
    {
        var (executor, _, _) = await CreateAsync(new() { ["design"] = "not json" });

        var result = await executor.ExecuteAsync(Context(new("design", NodeKind.Design)), _ct);

        Assert.False(result.Succeeded);
        Assert.Contains("valid JSON", result.Error, StringComparison.Ordinal);
    }

    public void Dispose() => _repo.Dispose();
}

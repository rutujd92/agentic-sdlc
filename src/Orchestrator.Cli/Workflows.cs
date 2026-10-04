using System.Text.Json;
using Anthropic;
using Orchestrator.Core.Agents;
using Orchestrator.Core.Execution;
using Orchestrator.Core.Governance;
using Orchestrator.Core.Graph;
using Orchestrator.Infrastructure.Agents;
using Orchestrator.Infrastructure.Storage;
using Orchestrator.Infrastructure.Workspace;

namespace Orchestrator.Cli;

/// <summary>How a run was started; persisted so approve/revise/resume rebuild the same workflow in a new process.</summary>
internal sealed record RunConfig(
    string Mode,
    string? Scenario = null,
    bool Live = false,
    bool Record = false,
    int DelayMs = 400,
    string[]? Failing = null,
    string[]? Flaky = null,
    string[]? Extras = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string PathFor(string runsRoot, string runId) => System.IO.Path.Combine(runsRoot, runId, "run.json");

    public async Task SaveAsync(string runsRoot, string runId)
    {
        Directory.CreateDirectory(System.IO.Path.Combine(runsRoot, runId));
        await File.WriteAllTextAsync(PathFor(runsRoot, runId), JsonSerializer.Serialize(this, Json));
    }

    public static async Task<RunConfig> LoadAsync(string runsRoot, string runId) =>
        File.Exists(PathFor(runsRoot, runId))
            ? JsonSerializer.Deserialize<RunConfig>(await File.ReadAllTextAsync(PathFor(runsRoot, runId)), Json)!
            : new RunConfig("simulated");
}

internal sealed record Scenario(string Name, string ChangeRequest)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<Scenario> LoadAsync(string directory) =>
        JsonSerializer.Deserialize<Scenario>(await File.ReadAllTextAsync(System.IO.Path.Combine(directory, "scenario.json")), Json)!;
}

internal static class Workflows
{
    public static async Task<(WorkflowDefinition Workflow, ICheckpointStore? Checkpoints)> BuildAsync(
        RunConfig config, string repositoryRoot, string runsRoot, string runId, string policyPath)
    {
        var policyGate = new PolicyGate(new PolicyEngine(await PolicyFile.LoadAsync(policyPath, CancellationToken.None)));
        return config.Mode == "scenario"
            ? await ScenarioAsync(config, repositoryRoot, runsRoot, runId, policyGate)
            : (Simulated(config, policyGate), null);
    }

    private static async Task<(WorkflowDefinition, ICheckpointStore)> ScenarioAsync(
        RunConfig config, string repositoryRoot, string runsRoot, string runId, PolicyGate policyGate)
    {
        var worktree = await GitWorktree.OpenAsync(repositoryRoot, runsRoot, runId, CancellationToken.None);
        var responses = System.IO.Path.Combine(config.Scenario!, "responses");
        IChatProvider chat = config.Live ? new ClaudeChatProvider(new AnthropicClient()) : new ReplayChatProvider(responses);
        if (config.Live && config.Record)
        {
            chat = new RecordingChatProvider(chat, responses);
        }

        var llm = new LlmNodeExecutor(chat, worktree);
        var validate = new ValidateExecutor(worktree);
        var migration = new MigrationExecutor(worktree);
        var merge = new MergeExecutor(worktree);
        var openQuestions = new OpenQuestionsGate();

        var workflow = new WorkflowDefinition(StandardSdlcGraph.Create(), n => n.Kind switch
        {
            NodeKind.Validate => validate,
            NodeKind.Migration => migration,
            NodeKind.Merge => merge,
            _ => llm,
        })
        {
            ExitGatesFor = n => n.Kind switch
            {
                NodeKind.Requirements => [openQuestions],
                NodeKind.Implement or NodeKind.Tests or NodeKind.Docs or NodeKind.Migration => [policyGate],
                _ => [],
            },
            RetryPolicyFor = n => LlmNodeExecutor.Supports(n.Kind) ? new RetryPolicy(2, TimeSpan.FromSeconds(2)) : RetryPolicy.None,
        };
        return (workflow, worktree);
    }

    private static WorkflowDefinition Simulated(RunConfig config, PolicyGate policyGate)
    {
        var delay = TimeSpan.FromMilliseconds(config.DelayMs);
        var extras = Set(config.Extras);
        var primary = new SimulatedExecutor(delay, Set(config.Failing), Set(config.Flaky), extras);
        var fallback = new SimulatedExecutor(delay, Set(config.Failing), implementExtras: extras);
        return new WorkflowDefinition(StandardSdlcGraph.Create(), _ => primary)
        {
            RetryPolicyFor = _ => new RetryPolicy(MaxAttempts: 2, InitialBackoff: TimeSpan.FromMilliseconds(250)),
            FallbacksFor = _ => [fallback],
            ExitGatesFor = n => n.Kind is NodeKind.Implement or NodeKind.Tests or NodeKind.Docs ? [policyGate] : [],
        };
    }

    private static HashSet<string> Set(string[]? values) => new(values ?? [], StringComparer.Ordinal);
}

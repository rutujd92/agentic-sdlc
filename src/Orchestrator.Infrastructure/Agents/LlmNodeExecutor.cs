using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Orchestrator.Core.Agents;
using Orchestrator.Core.Execution;
using Orchestrator.Core.Graph;
using Orchestrator.Infrastructure.Workspace;

namespace Orchestrator.Infrastructure.Agents;

/// <summary>
/// Runs an LLM agent for a node: builds the prompt (change request, upstream artifacts, feedback),
/// asks for schema-constrained JSON, and turns it into an artifact. Code agents return whole files,
/// which are confined to the agent's lane and applied in the run's worktree; the artifact is the git diff.
/// </summary>
public sealed partial class LlmNodeExecutor(IChatProvider chat, GitWorktree worktree) : INodeExecutor
{
    public static bool Supports(NodeKind kind) => AgentSpecs.Supports(kind);

    public async Task<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var spec = AgentSpecs.For(context.Node.Kind);
        var actor = $"agent:{context.Node.Kind.ToString().ToLowerInvariant()}";

        var request = new ChatRequest(
            context.RunId,
            context.Node.Id,
            $"{AgentSpecs.BaseSystemPrompt}\n{spec.Role}",
            await worktree.ReadContextAsync(cancellationToken),
            UserPrompt(context),
            spec.Schema,
            spec.Effort)
        { Feedback = context.Feedback };

        var response = await chat.CompleteAsync(request, cancellationToken);
        var telemetry = new Dictionary<string, string>
        {
            ["model"] = response.Model,
            ["inputTokens"] = response.InputTokens.ToString(CultureInfo.InvariantCulture),
            ["outputTokens"] = response.OutputTokens.ToString(CultureInfo.InvariantCulture),
        };

        try
        {
            using var json = JsonDocument.Parse(response.Json);
            var result = context.Node.Kind switch
            {
                NodeKind.Requirements => Success(context, RenderRequirements(json.RootElement), actor, "Requirements normalized."),
                NodeKind.Design => Design(context, json.RootElement, actor),
                NodeKind.ReleaseReadiness => ReleaseReadiness(context, json.RootElement, actor),
                _ => await CodeChangeAsync(context, json.RootElement, spec, actor, cancellationToken),
            };
            return result with { Telemetry = telemetry };
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return NodeResult.Failure($"Model output was not valid JSON for the {context.Node.Kind} schema: {ex.Message}", actor) with { Telemetry = telemetry };
        }
    }

    private static string UserPrompt(NodeExecutionContext context)
    {
        var prompt = new StringBuilder()
            .Append("# Change request\n").Append(context.ChangeRequest).Append("\n\n");
        foreach (var input in context.Inputs.Values)
        {
            prompt.Append("# Upstream artifact: ").Append(input.NodeId).Append('\n').Append(input.Content).Append("\n\n");
        }

        return prompt.Append("Produce your output for node '").Append(context.Node.Id).Append("'.").ToString();
    }

    private static NodeResult Success(NodeExecutionContext context, string content, string actor, string rationale) =>
        NodeResult.Success(Artifact.Create(context.Node.Id, content), actor, rationale);

    private static string RenderRequirements(JsonElement r) =>
        new StringBuilder("# Requirements\n\n")
            .Append(r.GetProperty("problem").GetString()).Append("\n\n")
            .Append(Section("Acceptance criteria", r.GetProperty("acceptanceCriteria")))
            .Append(Section("Assumptions", r.GetProperty("assumptions")))
            .Append(Section("Open questions", r.GetProperty("openQuestions")))
            .Append(Section("Out of scope", r.GetProperty("outOfScope")))
            .ToString();

    private static NodeResult Design(NodeExecutionContext context, JsonElement d, string actor)
    {
        var content = new StringBuilder("# Design\n\n")
            .Append(d.GetProperty("approach").GetString()).Append("\n\n")
            .Append(Section("Impacted files", d.GetProperty("impactedFiles")))
            .Append("## API and schema changes\n").Append(d.GetProperty("apiChanges").GetString()).Append("\n\n")
            .Append(Section("Risks", d.GetProperty("risks")));

        var result = Success(context, content.ToString(), actor, "Design produced.");
        var migrationName = d.GetProperty("migrationName").GetString() ?? string.Empty;
        if (!d.GetProperty("requiresMigration").GetBoolean())
        {
            return result;
        }

        if (!MigrationName().IsMatch(migrationName))
        {
            return NodeResult.Failure($"requiresMigration is true but migrationName '{migrationName}' is not a PascalCase identifier.", actor);
        }

        // The schema change is generated by EF tooling after the entity code exists, and must exist before validation.
        return result with
        {
            Plan = new GraphChange(
                [new WorkflowNode("migration", NodeKind.Migration) { DependsOn = ["implement"], Risk = RiskLevel.High, Description = migrationName }],
                [new DependencyEdge("validate", "migration")],
                $"Design changes the EF Core model; adding migration '{migrationName}' (generated by dotnet ef, requires approval)."),
        };
    }

    private static NodeResult ReleaseReadiness(NodeExecutionContext context, JsonElement r, string actor)
    {
        var content = new StringBuilder("# Release readiness\n\n").Append(r.GetProperty("summary").GetString()).Append("\n\n## Checklist\n");
        foreach (var item in r.GetProperty("checklist").EnumerateArray())
        {
            content.Append("- [").Append(item.GetProperty("status").GetString()).Append("] ").Append(item.GetProperty("item").GetString()).Append('\n');
        }

        content.Append('\n').Append(Section("Risks", r.GetProperty("risks")));
        return r.GetProperty("ready").GetBoolean()
            ? Success(context, content.ToString(), actor, "Ready to merge.")
            : NodeResult.Failure($"Release readiness: not ready. {r.GetProperty("summary").GetString()}", actor);
    }

    private async Task<NodeResult> CodeChangeAsync(NodeExecutionContext context, JsonElement change, AgentSpec spec, string actor, CancellationToken cancellationToken)
    {
        var files = change.GetProperty("files").EnumerateArray()
            .Select(f => new FileChange(f.GetProperty("path").GetString()!, f.GetProperty("content").GetString()!))
            .ToArray();

        var outOfLane = files.Where(f => !spec.Lane.Any(l => f.Path.Replace('\\', '/').StartsWith(l, StringComparison.Ordinal))).Select(f => f.Path).ToArray();
        if (outOfLane.Length > 0)
        {
            return NodeResult.Failure($"{context.Node.Kind} agent may only write under {string.Join(", ", spec.Lane)}; rejected: {string.Join(", ", outOfLane)}.", actor);
        }

        if (files.Length == 0)
        {
            return context.Node.Kind == NodeKind.Implement
                ? NodeResult.Failure("Implementer returned no file changes.", actor)
                : Success(context, "No changes.", actor, change.GetProperty("summary").GetString() ?? "No changes.");
        }

        var diff = await worktree.ApplyFilesAsync(files, cancellationToken);
        return Success(context, diff, actor, change.GetProperty("summary").GetString() ?? "Change applied.");
    }

    private static string Section(string title, JsonElement items)
    {
        var lines = items.EnumerateArray().Select(i => $"- {i.GetString()}").ToArray();
        return $"## {title}\n{(lines.Length == 0 ? "- None" : string.Join('\n', lines))}\n\n";
    }

    [GeneratedRegex("^[A-Z][A-Za-z0-9]{2,60}$", RegexOptions.CultureInvariant)]
    private static partial Regex MigrationName();
}

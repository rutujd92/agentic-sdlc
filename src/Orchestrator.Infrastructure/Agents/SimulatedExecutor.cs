using System.Globalization;
using System.Text;
using Orchestrator.Core.Execution;
using Orchestrator.Core.Graph;

namespace Orchestrator.Infrastructure.Agents;

/// <summary>
/// Stand-in agent for demos and engine tests before real agents exist: waits a short time and returns a
/// placeholder artifact listing its inputs. Nodes in <paramref name="failingNodes"/> always fail;
/// nodes in <paramref name="flakyNodes"/> fail on their first attempt only (to demonstrate retries).
/// </summary>
public sealed class SimulatedExecutor(
    TimeSpan delay,
    IReadOnlySet<string>? failingNodes = null,
    IReadOnlySet<string>? flakyNodes = null,
    IReadOnlySet<string>? implementExtras = null) : INodeExecutor
{
    public const string Actor = "agent:simulated";

    public async Task<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        await Task.Delay(delay, cancellationToken);

        if (failingNodes?.Contains(context.Node.Id) == true)
        {
            return NodeResult.Failure($"Simulated failure in '{context.Node.Id}'.", Actor);
        }

        if (flakyNodes?.Contains(context.Node.Id) == true && context.Attempt == 1)
        {
            return NodeResult.Failure($"Simulated transient error in '{context.Node.Id}' (attempt 1).", Actor);
        }

        if (context.Node.Kind == NodeKind.Implement)
        {
            return NodeResult.Success(Artifact.Create(context.Node.Id, ImplementDiff()), Actor, "Simulated implementation diff.");
        }

        if (context.Node.Kind == NodeKind.Migration)
        {
            var migration = File("src/UrlShortener.Infrastructure/Persistence/Migrations/20261004000000_AddClicks.cs", "migrationBuilder.CreateTable(name: \"clicks\");");
            return NodeResult.Success(Artifact.Create(context.Node.Id, migration), Actor, "Simulated migration.");
        }

        var content = new StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"# {context.Node.Id}")
            .AppendLine()
            .AppendLine(CultureInfo.InvariantCulture, $"Simulated {context.Node.Kind} output for: {context.ChangeRequest}")
            .AppendLine()
            .AppendLine("Inputs:");
        foreach (var input in context.Inputs.Values)
        {
            content.AppendLine(CultureInfo.InvariantCulture, $"- {input.NodeId} ({input.Hash[..12]})");
        }

        if (context.Node.Kind == NodeKind.Requirements && context.Feedback is not null)
        {
            content.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"Clarification applied: {context.Feedback}");
        }

        var result = NodeResult.Success(Artifact.Create(context.Node.Id, content.ToString()), Actor, $"Simulated {context.Node.Kind} completed.");
        return context.Node.Kind == NodeKind.Design && implementExtras?.Contains("migration") == true
            ? result with
            {
                Plan = new GraphChange(
                    [new WorkflowNode("migration", NodeKind.Migration) { DependsOn = [context.Node.Id], Risk = RiskLevel.High, Description = "Add clicks table." }],
                    [new DependencyEdge("implement", "migration")],
                    "Design requires a schema change: the clicks table must exist before the implementation uses it."),
            }
            : result;
    }

    /// <summary>A small unified diff; extras (migration, package, secret) exercise the policy engine.</summary>
    private string ImplementDiff()
    {
        var diff = new StringBuilder()
            .Append(File("src/UrlShortener.Api/Links/LinkEndpoints.cs", "// simulated change"));
        if (implementExtras?.Contains("package") == true)
        {
            diff.Append(File("src/UrlShortener.Api/UrlShortener.Api.csproj", "<PackageReference Include=\"QRCoder\" Version=\"1.6.0\" />"));
        }

        if (implementExtras?.Contains("secret") == true)
        {
            diff.Append(File("src/UrlShortener.Api/appsettings.json", "\"Shortener\": \"Host=prod;Password=SuperSecret123\""));
        }

        return diff.ToString();
    }

    private static string File(string path, string added) =>
        $"diff --git a/{path} b/{path}\n--- a/{path}\n+++ b/{path}\n@@ -1 +1,2 @@\n context\n+{added}\n";
}

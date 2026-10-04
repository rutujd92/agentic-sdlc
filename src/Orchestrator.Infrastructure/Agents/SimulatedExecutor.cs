using System.Globalization;
using System.Text;
using Orchestrator.Core.Execution;

namespace Orchestrator.Infrastructure.Agents;

/// <summary>
/// Stand-in agent for demos and engine tests before real agents exist: waits a short time and returns a
/// placeholder artifact listing its inputs. Nodes in <paramref name="failingNodes"/> always fail;
/// nodes in <paramref name="flakyNodes"/> fail on their first attempt only (to demonstrate retries).
/// </summary>
public sealed class SimulatedExecutor(TimeSpan delay, IReadOnlySet<string>? failingNodes = null, IReadOnlySet<string>? flakyNodes = null) : INodeExecutor
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

        return NodeResult.Success(Artifact.Create(context.Node.Id, content.ToString()), Actor, $"Simulated {context.Node.Kind} completed.");
    }
}

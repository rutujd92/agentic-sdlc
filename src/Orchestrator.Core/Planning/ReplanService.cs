using Orchestrator.Core.Events;
using Orchestrator.Core.State;

namespace Orchestrator.Core.Planning;

/// <summary>
/// Human-driven re-planning: revise a completed node with new guidance (e.g. answers to the
/// requirements' open questions). On resume the node re-runs with the guidance; if its output hash
/// changes, the engine invalidates and re-runs dependents level by level until outputs stabilize.
/// </summary>
public sealed class ReplanService(IEventStore eventStore, TimeProvider timeProvider)
{
    public async Task ReviseAsync(string runId, string nodeId, string actor, string guidance, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(guidance);
        var state = RunState.Rebuild(await eventStore.ReadAsync(runId, cancellationToken));

        if (!state.Nodes.TryGetValue(nodeId, out var node))
        {
            throw new InvalidOperationException($"Run '{runId}' has no node '{nodeId}'.");
        }

        var revisable = node.Status is NodeStatus.Succeeded or NodeStatus.Failed
            || node is { Status: NodeStatus.AwaitingApproval, ApprovalPhase: "output" };
        if (!revisable)
        {
            throw new InvalidOperationException($"Node '{nodeId}' is {node.Status}; only nodes that have produced output can be revised.");
        }

        if (state.Status is not (RunStatus.Paused or RunStatus.Stopped))
        {
            throw new InvalidOperationException($"Run '{runId}' is {state.Status}; revisions are recorded on paused or stopped runs.");
        }

        await eventStore.AppendAsync(
            new RunEvent(state.LastSequence + 1, runId, RunEventType.NodeInvalidated, timeProvider.GetUtcNow(), $"human:{actor}")
            {
                NodeId = nodeId,
                Message = guidance,
                Data = new Dictionary<string, string> { ["reason"] = "revised", ["guidance"] = guidance },
            },
            cancellationToken);
    }
}

using Orchestrator.Core.Events;
using Orchestrator.Core.State;

namespace Orchestrator.Core.Governance;

/// <summary>
/// Records human decisions on a paused run. Decisions are appended to the run's event log with the
/// approver's identity; the engine acts on them when the run is resumed.
/// </summary>
public sealed class ApprovalService(IEventStore eventStore, TimeProvider timeProvider)
{
    public Task GrantAsync(string runId, string nodeId, string approver, string? note, CancellationToken cancellationToken) =>
        DecideAsync(runId, nodeId, approver, RunEventType.ApprovalGranted, note, cancellationToken);

    public Task RejectAsync(string runId, string nodeId, string approver, string reason, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return DecideAsync(runId, nodeId, approver, RunEventType.ApprovalRejected, reason, cancellationToken);
    }

    private async Task DecideAsync(string runId, string nodeId, string approver, RunEventType decision, string? message, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(approver);
        var events = await eventStore.ReadAsync(runId, cancellationToken);
        var state = RunState.Rebuild(events);

        if (!state.Nodes.TryGetValue(nodeId, out var node) || node.Status != NodeStatus.AwaitingApproval || node.ApprovalGranted)
        {
            throw new InvalidOperationException($"Node '{nodeId}' in run '{runId}' is not awaiting approval.");
        }

        if (state.Status != RunStatus.Paused)
        {
            throw new InvalidOperationException($"Run '{runId}' is {state.Status}; decisions are recorded on paused runs only.");
        }

        await eventStore.AppendAsync(
            new RunEvent(state.LastSequence + 1, runId, decision, timeProvider.GetUtcNow(), $"human:{approver}")
            {
                NodeId = nodeId,
                Message = message,
                Data = new Dictionary<string, string> { ["phase"] = node.ApprovalPhase! },
            },
            cancellationToken);
    }
}

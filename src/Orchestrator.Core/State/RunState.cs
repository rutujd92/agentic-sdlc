using Orchestrator.Core.Events;

namespace Orchestrator.Core.State;

public enum RunStatus
{
    Running,
    Paused,
    Stopped,
    Succeeded,
    Failed,
    RolledBack,
}

public enum NodeStatus
{
    Pending,
    Ready,
    Running,
    AwaitingApproval,
    Succeeded,
    Failed,
    Skipped,
    Invalidated,
}

public sealed record NodeState(NodeStatus Status, int Attempts = 0, string? OutputHash = null, string? Error = null);

/// <summary>
/// Projection of a run's event log. The engine applies each event as it is written, and
/// <see cref="Rebuild"/> replays a stored log, so live and rebuilt state are produced by the same code.
/// </summary>
public sealed class RunState
{
    private readonly Dictionary<string, NodeState> _nodes = new(StringComparer.Ordinal);

    public string RunId { get; private set; } = string.Empty;

    public string ChangeRequest { get; private set; } = string.Empty;

    public RunStatus Status { get; private set; } = RunStatus.Running;

    public IReadOnlyDictionary<string, NodeState> Nodes => _nodes;

    public long LastSequence { get; private set; }

    public string? LastCheckpointId { get; private set; }

    public int TotalAttempts => _nodes.Values.Sum(n => n.Attempts);

    public static RunState Rebuild(IEnumerable<RunEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        var state = new RunState();
        foreach (var runEvent in events)
        {
            state.Apply(runEvent);
        }

        return state;
    }

    public void Apply(RunEvent runEvent)
    {
        ArgumentNullException.ThrowIfNull(runEvent);
        LastSequence = runEvent.Sequence;
        switch (runEvent.Type)
        {
            case RunEventType.RunStarted:
                RunId = runEvent.RunId;
                ChangeRequest = runEvent.Message ?? string.Empty;
                foreach (var id in runEvent.Data["nodes"].Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    _nodes[id] = new NodeState(NodeStatus.Pending);
                }

                break;
            case RunEventType.NodeReady:
                Update(runEvent, n => n with { Status = NodeStatus.Ready });
                break;
            case RunEventType.NodeStarted:
                Update(runEvent, n => n with { Status = NodeStatus.Running, Attempts = n.Attempts + 1, Error = null });
                break;
            case RunEventType.NodeSucceeded:
                Update(runEvent, n => n with { Status = NodeStatus.Succeeded, OutputHash = runEvent.Data.GetValueOrDefault("outputHash") });
                break;
            case RunEventType.NodeFailed:
                Update(runEvent, n => n with { Status = NodeStatus.Failed, Error = runEvent.Message });
                break;
            case RunEventType.NodeSkipped:
                Update(runEvent, n => n with { Status = NodeStatus.Skipped, Error = runEvent.Message });
                break;
            case RunEventType.CheckpointCreated:
                LastCheckpointId = runEvent.Data["checkpointId"];
                break;
            case RunEventType.SafeStopped:
                Status = RunStatus.Stopped;
                break;
            case RunEventType.RunResumed:
                Status = RunStatus.Running;
                break;
            case RunEventType.RolledBack:
                Status = RunStatus.RolledBack;
                break;
            case RunEventType.RunCompleted:
                Status = RunStatus.Succeeded;
                break;
            case RunEventType.RunFailed:
                Status = RunStatus.Failed;
                break;
            default:
                break;
        }
    }

    private void Update(RunEvent runEvent, Func<NodeState, NodeState> change)
    {
        var id = runEvent.NodeId ?? throw new InvalidOperationException($"{runEvent.Type} event #{runEvent.Sequence} has no node id.");
        _nodes[id] = change(_nodes[id]);
    }
}

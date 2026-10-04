using Orchestrator.Core.Graph;

namespace Orchestrator.Core.Execution;

public interface INodeExecutor
{
    Task<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken);
}

/// <param name="Inputs">Artifacts of every upstream (transitive) dependency, keyed by node id.</param>
/// <param name="Attempt">1-based attempt number across primary and fallback executors.</param>
public sealed record NodeExecutionContext(
    string RunId,
    WorkflowNode Node,
    string ChangeRequest,
    IReadOnlyDictionary<string, Artifact> Inputs,
    int Attempt)
{
    /// <summary>Why the previous attempt failed (executor error or failed gate), so the next attempt can correct it.</summary>
    public string? Feedback { get; init; }
}

public sealed record NodeResult(bool Succeeded, Artifact? Output, string? Error, string Actor, string? Rationale)
{
    /// <summary>Optional re-plan proposed by this node (e.g. design discovers a migration is needed).</summary>
    public GraphChange? Plan { get; init; }

    /// <summary>Execution telemetry (model, token usage, durations) recorded on the NodeSucceeded event.</summary>
    public IReadOnlyDictionary<string, string> Telemetry { get; init; } = new Dictionary<string, string>();

    public static NodeResult Success(Artifact output, string actor, string? rationale = null) => new(true, output, null, actor, rationale);

    public static NodeResult Failure(string error, string actor) => new(false, null, error, actor, null);
}

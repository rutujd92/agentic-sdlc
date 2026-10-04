using Orchestrator.Core.Graph;

namespace Orchestrator.Core.Execution;

public interface INodeExecutor
{
    Task<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken);
}

/// <param name="Inputs">Artifacts of every upstream (transitive) dependency, keyed by node id.</param>
/// <param name="Attempt">1-based attempt number.</param>
public sealed record NodeExecutionContext(
    string RunId,
    WorkflowNode Node,
    string ChangeRequest,
    IReadOnlyDictionary<string, Artifact> Inputs,
    int Attempt);

public sealed record NodeResult(bool Succeeded, Artifact? Output, string? Error, string Actor, string? Rationale)
{
    public static NodeResult Success(Artifact output, string actor, string? rationale = null) => new(true, output, null, actor, rationale);

    public static NodeResult Failure(string error, string actor) => new(false, null, error, actor, null);
}

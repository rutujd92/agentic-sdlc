using Orchestrator.Core.Graph;

namespace Orchestrator.Core.Execution;

/// <summary>Entry gates run before a node starts; exit gates run on its output before it may succeed.</summary>
public interface IGate
{
    string Name { get; }

    Task<GateResult> EvaluateAsync(GateContext context, CancellationToken cancellationToken);
}

/// <param name="Output">Null for entry gates.</param>
public sealed record GateContext(string RunId, WorkflowNode Node, IReadOnlyDictionary<string, Artifact> Inputs, Artifact? Output);

public sealed record GateResult(bool Passed, string Reason)
{
    /// <summary>The gate passed, but a human must review the output before the node may succeed.</summary>
    public bool RequiresApproval { get; init; }

    /// <summary>Policy findings to record in the audit log (one PolicyViolation event each).</summary>
    public IReadOnlyList<string> Violations { get; init; } = [];

    public static GateResult Pass(string reason) => new(true, reason);

    public static GateResult Fail(string reason) => new(false, reason);
}

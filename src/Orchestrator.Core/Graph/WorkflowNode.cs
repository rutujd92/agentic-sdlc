namespace Orchestrator.Core.Graph;

/// <summary>A step in the SDLC graph. Executors, gates and retry policies attach to nodes by id.</summary>
public sealed record WorkflowNode(string Id, NodeKind Kind)
{
    public IReadOnlyList<string> DependsOn { get; init; } = [];

    public RiskLevel Risk { get; init; } = RiskLevel.Low;

    /// <summary>When true the engine pauses before completing the node until a human approves.</summary>
    public bool RequiresApproval { get; init; }

    public string? Description { get; init; }
}

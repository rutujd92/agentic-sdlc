namespace Orchestrator.Core.Graph;

public enum NodeKind
{
    Requirements,
    Clarification,
    Design,
    Implement,
    Tests,
    Docs,
    Migration,
    Validate,
    ReleaseReadiness,
    Merge,
    Custom,
}

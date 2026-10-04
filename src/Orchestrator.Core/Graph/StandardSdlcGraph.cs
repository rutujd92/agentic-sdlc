namespace Orchestrator.Core.Graph;

/// <summary>
/// Default SDLC pipeline: requirements -> design -> {implement || tests || docs} -> validate (join)
/// -> release readiness -> merge (human approval). Planners may add nodes (e.g. clarification, migration).
/// </summary>
public static class StandardSdlcGraph
{
    public const string Requirements = "requirements";
    public const string Design = "design";
    public const string Implement = "implement";
    public const string Tests = "tests";
    public const string Docs = "docs";
    public const string Validate = "validate";
    public const string ReleaseReadiness = "release-readiness";
    public const string Merge = "merge";

    public static WorkflowGraph Create() => WorkflowGraph.Create(
    [
        new(Requirements, NodeKind.Requirements) { Description = "Normalize the change request: problem, acceptance criteria, ambiguities, assumptions." },
        new(Design, NodeKind.Design) { DependsOn = [Requirements], Risk = RiskLevel.Medium, Description = "Impacted modules, API/schema changes, risks." },
        new(Implement, NodeKind.Implement) { DependsOn = [Design], Risk = RiskLevel.Medium, Description = "Produce the code change." },
        new(Tests, NodeKind.Tests) { DependsOn = [Design], Description = "Produce unit and integration tests." },
        new(Docs, NodeKind.Docs) { DependsOn = [Design], Description = "Update README and changelog." },
        new(Validate, NodeKind.Validate) { DependsOn = [Implement, Tests, Docs], Description = "Build, test, secret scan, policy checks on the combined change." },
        new(ReleaseReadiness, NodeKind.ReleaseReadiness) { DependsOn = [Validate], Description = "Release checklist and risk summary." },
        new(Merge, NodeKind.Merge) { DependsOn = [ReleaseReadiness], Risk = RiskLevel.High, RequiresApproval = true, Description = "Merge to main after human sign-off." },
    ]);
}

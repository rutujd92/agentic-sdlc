namespace Orchestrator.Core.Governance;

/// <summary>Ordered by severity, so the overall outcome is the maximum finding.</summary>
public enum PolicyOutcome
{
    Allow,
    RequireApproval,
    Block,
}

public sealed record PolicyFinding(string Rule, PolicyOutcome Outcome, string Message);

public sealed record PolicyDecision(PolicyOutcome Outcome, IReadOnlyList<PolicyFinding> Findings);

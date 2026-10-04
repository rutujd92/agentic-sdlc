using Orchestrator.Core.Execution;

namespace Orchestrator.Core.Governance;

/// <summary>Exit gate that applies <see cref="PolicyEngine"/> to a node's output.</summary>
public sealed class PolicyGate(PolicyEngine policy) : IGate
{
    public string Name => "policy";

    public Task<GateResult> EvaluateAsync(GateContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Output is null)
        {
            return Task.FromResult(GateResult.Pass("No output to check."));
        }

        var decision = policy.Evaluate(context.Output.Content);
        var violations = decision.Findings.Select(f => $"[{f.Outcome}] {f.Rule}: {f.Message}").ToArray();
        var summary = string.Join(" ", decision.Findings.Select(f => $"{f.Rule}: {f.Message}"));

        return Task.FromResult(decision.Outcome switch
        {
            PolicyOutcome.Block => GateResult.Fail(summary) with { Violations = violations },
            PolicyOutcome.RequireApproval => GateResult.Pass(summary) with { RequiresApproval = true, Violations = violations },
            _ => GateResult.Pass("Policy checks passed."),
        });
    }
}

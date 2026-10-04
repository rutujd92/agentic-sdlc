using Orchestrator.Core.Execution;

namespace Orchestrator.Core.Governance;

/// <summary>
/// Exit gate for requirements: unresolved open questions mean the request is ambiguous, so a human
/// must either accept the stated assumptions (approve) or answer them (revise with guidance).
/// </summary>
public sealed class OpenQuestionsGate : IGate
{
    public string Name => "open-questions";

    public Task<GateResult> EvaluateAsync(GateContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var questions = OpenQuestions(context.Output?.Content ?? string.Empty);
        return Task.FromResult(questions.Count == 0
            ? GateResult.Pass("No open questions.")
            : GateResult.Pass($"{questions.Count} open question(s) need a human answer: {string.Join(" | ", questions)}") with { RequiresApproval = true });
    }

    public static IReadOnlyList<string> OpenQuestions(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var questions = new List<string>();
        var inSection = false;
        foreach (var line in markdown.Split('\n').Select(l => l.TrimEnd('\r')))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                inSection = line.Equals("## Open questions", StringComparison.OrdinalIgnoreCase);
            }
            else if (inSection && line.StartsWith("- ", StringComparison.Ordinal) && !line[2..].Trim().Equals("None", StringComparison.OrdinalIgnoreCase))
            {
                questions.Add(line[2..].Trim());
            }
        }

        return questions;
    }
}

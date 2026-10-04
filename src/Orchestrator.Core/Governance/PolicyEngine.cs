using System.Text.RegularExpressions;

namespace Orchestrator.Core.Governance;

/// <summary>
/// Evaluates an agent's unified diff against <see cref="PolicyRules"/>: change control (allowed, blocked and
/// approval paths), secret scanning of added lines, and license checks for new package references.
/// Artifacts that are not diffs (requirements, design notes) touch no files and are allowed.
/// </summary>
public sealed partial class PolicyEngine
{
    private readonly PolicyRules _rules;
    private readonly Regex[] _secrets;

    public PolicyEngine(PolicyRules rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _rules = rules;
        _secrets = rules.SecretPatterns.Select(p => new Regex(p, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))).ToArray();
    }

    public PolicyDecision Evaluate(string artifactContent)
    {
        ArgumentNullException.ThrowIfNull(artifactContent);
        var diff = UnifiedDiff.Parse(artifactContent);
        var findings = new List<PolicyFinding>();

        foreach (var path in diff.Files)
        {
            if (_rules.BlockedPaths.Any(p => Glob.IsMatch(p, path)))
            {
                findings.Add(new("blocked-paths", PolicyOutcome.Block, $"'{path}' is a protected path agents may not change."));
            }
            else if (!_rules.AllowedPaths.Any(p => Glob.IsMatch(p, path)))
            {
                findings.Add(new("allowed-paths", PolicyOutcome.Block, $"'{path}' is outside the allowed paths for this change."));
            }
            else if (_rules.ApprovalPaths.Any(p => Glob.IsMatch(p, path)))
            {
                findings.Add(new("approval-paths", PolicyOutcome.RequireApproval, $"'{path}' is high impact and needs human review."));
            }
        }

        foreach (var (path, line) in diff.AddedLines)
        {
            // Report where, never what: the finding must not leak the secret into the audit log.
            if (_secrets.FirstOrDefault(r => r.IsMatch(line)) is { } secret)
            {
                findings.Add(new("secrets", PolicyOutcome.Block, $"Possible secret added in '{path}' (pattern #{Array.IndexOf(_secrets, secret) + 1})."));
            }

            if (PackageReference().Match(line) is { Success: true } package)
            {
                findings.Add(CheckLicense(package.Groups["name"].Value));
            }
        }

        var outcome = findings.Count == 0 ? PolicyOutcome.Allow : findings.Max(f => f.Outcome);
        return new PolicyDecision(outcome, findings);
    }

    private PolicyFinding CheckLicense(string package)
    {
        if (!_rules.PackageLicenses.TryGetValue(package, out var license))
        {
            return new("licenses", PolicyOutcome.Block, $"New dependency {package} has an unknown license; add it to policies.json after review.");
        }

        return _rules.AllowedLicenses.Contains(license, StringComparer.OrdinalIgnoreCase)
            ? new("new-dependency", PolicyOutcome.RequireApproval, $"New dependency {package} ({license}) needs human sign-off.")
            : new("licenses", PolicyOutcome.Block, $"New dependency {package} ({license}) has a license that is not allowed.");
    }

    [GeneratedRegex("<PackageReference\\s+Include=\"(?<name>[^\"]+)\"", RegexOptions.CultureInvariant)]
    private static partial Regex PackageReference();
}

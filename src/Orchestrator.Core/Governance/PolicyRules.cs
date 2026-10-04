namespace Orchestrator.Core.Governance;

/// <summary>Declarative guardrails, loaded from <c>policies.json</c>. Paths use globs relative to the repository root.</summary>
public sealed class PolicyRules
{
    /// <summary>Change control: agents may only touch these paths.</summary>
    public IReadOnlyList<string> AllowedPaths { get; init; } = [];

    /// <summary>High-impact paths (e.g. migrations): allowed, but a human must review the change.</summary>
    public IReadOnlyList<string> ApprovalPaths { get; init; } = [];

    /// <summary>Never writable by agents, even when inside an allowed path (CI config, keys).</summary>
    public IReadOnlyList<string> BlockedPaths { get; init; } = [];

    /// <summary>Regexes matched against added lines only.</summary>
    public IReadOnlyList<string> SecretPatterns { get; init; } = [];

    /// <summary>SPDX ids allowed for new dependencies.</summary>
    public IReadOnlyList<string> AllowedLicenses { get; init; } = [];

    /// <summary>Known package -> SPDX license. Packages missing here are treated as unknown and blocked.</summary>
    public IReadOnlyDictionary<string, string> PackageLicenses { get; init; } = new Dictionary<string, string>();
}

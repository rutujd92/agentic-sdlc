namespace Orchestrator.Core.Governance;

/// <summary>Minimal unified-diff reader: touched files and added lines (with their file).</summary>
public sealed record UnifiedDiff(IReadOnlyList<string> Files, IReadOnlyList<(string Path, string Line)> AddedLines)
{
    public static UnifiedDiff Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var files = new List<string>();
        var added = new List<(string, string)>();
        string? current = null;

        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                var bPath = line.LastIndexOf(" b/", StringComparison.Ordinal);
                current = bPath >= 0 ? line[(bPath + 3)..].TrimEnd('\r') : null;
                if (current is not null && !files.Contains(current, StringComparer.Ordinal))
                {
                    files.Add(current);
                }
            }
            else if (current is not null && line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal))
            {
                added.Add((current, line[1..].TrimEnd('\r')));
            }
        }

        return new UnifiedDiff(files, added);
    }
}

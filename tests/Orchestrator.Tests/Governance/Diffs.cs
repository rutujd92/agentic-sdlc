namespace Orchestrator.Tests.Governance;

/// <summary>Builds unified diffs the way an implementing agent would emit them.</summary>
public static class Diffs
{
    public static string File(string path, params string[] addedLines) =>
        $"diff --git a/{path} b/{path}\n--- a/{path}\n+++ b/{path}\n@@ -1,1 +1,{addedLines.Length + 1} @@\n context\n"
        + string.Concat(addedLines.Select(l => $"+{l}\n"));

    public static string Combine(params string[] diffs) => string.Concat(diffs);
}

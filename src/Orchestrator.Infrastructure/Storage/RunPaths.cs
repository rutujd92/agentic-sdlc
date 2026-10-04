namespace Orchestrator.Infrastructure.Storage;

internal static class RunPaths
{
    public static string Run(string root, string runId) => Path.Combine(root, SafeSegment(runId));

    public static string Events(string root, string runId) => Path.Combine(Run(root, runId), "events.jsonl");

    public static string Artifact(string root, string runId, string nodeId) =>
        Path.Combine(Run(root, runId), "artifacts", SafeSegment(nodeId) + ".json");

    // Ids become path segments; reject anything that could escape the runs directory.
    private static string SafeSegment(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Contains("..", StringComparison.Ordinal) || value.IndexOfAny(['/', '\\', ':']) >= 0)
        {
            throw new ArgumentException($"Invalid id '{value}'.", nameof(value));
        }

        return value;
    }
}

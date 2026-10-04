using System.Text.Json;
using Orchestrator.Core.Events;

namespace Orchestrator.Infrastructure.Storage;

/// <summary>
/// Append-only JSON Lines log at <c>&lt;root&gt;/&lt;runId&gt;/events.jsonl</c>: one event per line,
/// human-readable, diff-able and greppable, which suits an audit trail. The engine serializes appends per run.
/// </summary>
public sealed class JsonlEventStore(string rootDirectory) : IEventStore
{
    public async Task AppendAsync(RunEvent runEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runEvent);
        var path = RunPaths.Events(rootDirectory, runEvent.RunId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.AppendAllTextAsync(path, JsonSerializer.Serialize(runEvent, OrchestratorJson.Options) + "\n", cancellationToken);
    }

    public async Task<IReadOnlyList<RunEvent>> ReadAsync(string runId, CancellationToken cancellationToken)
    {
        var path = RunPaths.Events(rootDirectory, runId);
        if (!File.Exists(path))
        {
            return [];
        }

        var lines = await File.ReadAllLinesAsync(path, cancellationToken);
        return lines
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => JsonSerializer.Deserialize<RunEvent>(l, OrchestratorJson.Options)!)
            .ToArray();
    }
}

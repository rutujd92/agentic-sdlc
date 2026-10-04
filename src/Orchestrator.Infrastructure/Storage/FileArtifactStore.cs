using System.Text.Json;
using Orchestrator.Core.Execution;

namespace Orchestrator.Infrastructure.Storage;

/// <summary>Stores each node's artifact at <c>&lt;root&gt;/&lt;runId&gt;/artifacts/&lt;nodeId&gt;.json</c>.</summary>
public sealed class FileArtifactStore(string rootDirectory) : IArtifactStore
{
    public async Task SaveAsync(string runId, Artifact artifact, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var path = RunPaths.Artifact(rootDirectory, runId, artifact.NodeId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(artifact, OrchestratorJson.Options), cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, Artifact>> ListAsync(string runId, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(RunPaths.Artifact(rootDirectory, runId, "x"))!;
        var artifacts = new Dictionary<string, Artifact>(StringComparer.Ordinal);
        if (Directory.Exists(directory))
        {
            foreach (var file in Directory.GetFiles(directory, "*.json"))
            {
                var artifact = JsonSerializer.Deserialize<Artifact>(await File.ReadAllTextAsync(file, cancellationToken), OrchestratorJson.Options)!;
                artifacts[artifact.NodeId] = artifact;
            }
        }

        return artifacts;
    }

    public async Task<Artifact?> GetAsync(string runId, string nodeId, CancellationToken cancellationToken)
    {
        var path = RunPaths.Artifact(rootDirectory, runId, nodeId);
        return File.Exists(path)
            ? JsonSerializer.Deserialize<Artifact>(await File.ReadAllTextAsync(path, cancellationToken), OrchestratorJson.Options)
            : null;
    }
}

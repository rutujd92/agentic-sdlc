using System.Text.Json;
using Orchestrator.Core.Governance;

namespace Orchestrator.Infrastructure.Storage;

public static class PolicyFile
{
    public static async Task<PolicyRules> LoadAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<PolicyRules>(stream, OrchestratorJson.Options, cancellationToken)
            ?? throw new InvalidDataException($"Policy file '{path}' is empty.");
    }
}

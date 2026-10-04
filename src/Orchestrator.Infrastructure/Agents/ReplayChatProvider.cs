using System.Text.Json;
using Orchestrator.Core.Agents;
using Orchestrator.Infrastructure.Storage;

namespace Orchestrator.Infrastructure.Agents;

/// <summary>Serves recorded responses: deterministic, offline, free, and no API key needed (the demo default).</summary>
public sealed class ReplayChatProvider(string responsesDirectory) : IChatProvider
{
    public async Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var path = Path.Combine(responsesDirectory, RecordedResponse.FileName(request));
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"No recorded response for node '{request.NodeId}'{(request.Feedback is null ? string.Empty : " with this feedback")} at {path}. " +
                "Record one with --live --record.", path);
        }

        var recorded = JsonSerializer.Deserialize<RecordedResponse>(await File.ReadAllTextAsync(path, cancellationToken), OrchestratorJson.Options)!;
        return new ChatResponse(recorded.Response, $"replay:{recorded.Model}", recorded.InputTokens, recorded.OutputTokens);
    }
}

using System.Text.Json;
using Orchestrator.Core.Agents;
using Orchestrator.Infrastructure.Storage;

namespace Orchestrator.Infrastructure.Agents;

/// <summary>Decorator that saves live responses so the run can later be replayed exactly.</summary>
public sealed class RecordingChatProvider(IChatProvider inner, string responsesDirectory) : IChatProvider
{
    private static readonly JsonSerializerOptions Indented = new(OrchestratorJson.Options) { WriteIndented = true };

    public async Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var response = await inner.CompleteAsync(request, cancellationToken);
        Directory.CreateDirectory(responsesDirectory);
        var recorded = new RecordedResponse(request.NodeId, request.Feedback, response.Model, response.InputTokens, response.OutputTokens, response.Json);
        await File.WriteAllTextAsync(Path.Combine(responsesDirectory, RecordedResponse.FileName(request)), JsonSerializer.Serialize(recorded, Indented), cancellationToken);
        return response;
    }
}

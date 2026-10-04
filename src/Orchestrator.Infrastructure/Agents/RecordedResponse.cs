using System.Security.Cryptography;
using System.Text;
using Orchestrator.Core.Agents;

namespace Orchestrator.Infrastructure.Agents;

/// <summary>One recorded model response, stored as <c>&lt;nodeId&gt;[.&lt;feedbackHash&gt;].json</c> in a scenario's responses folder.</summary>
internal sealed record RecordedResponse(string NodeId, string? Feedback, string Model, long InputTokens, long OutputTokens, string Response)
{
    // Keyed by node + feedback (not the full prompt): replays stay valid when unrelated repository files change,
    // while a revision or retry with different guidance gets its own recording.
    public static string FileName(ChatRequest request) =>
        request.Feedback is null
            ? $"{request.NodeId}.json"
            : $"{request.NodeId}.{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(request.Feedback)))[..8]}.json";
}

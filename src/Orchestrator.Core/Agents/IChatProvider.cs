namespace Orchestrator.Core.Agents;

/// <summary>Model access behind one seam: live Claude, recorded replay, or a test fake.</summary>
public interface IChatProvider
{
    Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken);
}

public enum AgentEffort
{
    Low,
    Medium,
    High,
}

/// <param name="Context">Large, stable repository context; providers may cache it (prompt caching).</param>
/// <param name="JsonSchema">JSON Schema the response must satisfy (structured output).</param>
public sealed record ChatRequest(
    string RunId,
    string NodeId,
    string SystemPrompt,
    string Context,
    string UserPrompt,
    string JsonSchema,
    AgentEffort Effort)
{
    /// <summary>Retry feedback or human guidance; part of the replay key.</summary>
    public string? Feedback { get; init; }
}

public sealed record ChatResponse(string Json, string Model, long InputTokens, long OutputTokens);

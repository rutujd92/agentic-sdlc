using System.Text.Json;
using Anthropic;
using Anthropic.Models.Beta.Messages;
using Orchestrator.Core.Agents;

namespace Orchestrator.Infrastructure.Agents;

/// <summary>
/// Live agent calls through the official Anthropic SDK (key from ANTHROPIC_API_KEY).
/// Structured outputs constrain each agent to its JSON schema; the repository context is a cached
/// system block (identical across the nodes of a run); server-side fallbacks re-serve a refused request.
/// </summary>
public sealed class ClaudeChatProvider(AnthropicClient client, string model = ClaudeChatProvider.DefaultModel) : IChatProvider
{
    public const string DefaultModel = "claude-opus-5-5";

    public async Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var userPrompt = request.Feedback is null
            ? request.UserPrompt
            : $"{request.UserPrompt}\n\n## Feedback on the previous attempt / human guidance\n{request.Feedback}";

        var message = await client.Beta.Messages.Create(new MessageCreateParams
        {
            Model = model,
            MaxTokens = 16000,
            Betas = ["server-side-fallback-2026-07-01"],
            Fallbacks = new Default(),
            System = new List<BetaTextBlockParam>
            {
                new() { Text = request.SystemPrompt },
                new() { Text = $"<repository>\n{request.Context}\n</repository>", CacheControl = new BetaCacheControlEphemeral() },
            },
            OutputConfig = new BetaOutputConfig
            {
                Effort = request.Effort switch
                {
                    AgentEffort.Low => Effort.Low,
                    AgentEffort.Medium => Effort.Medium,
                    _ => Effort.High,
                },
                Format = new BetaJsonOutputFormat
                {
                    Schema = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(request.JsonSchema)!,
                },
            },
            Messages = [new() { Role = Role.User, Content = userPrompt }],
        }, cancellationToken: cancellationToken);

        if (message.StopReason == "refusal")
        {
            throw new InvalidOperationException($"Model declined the request for '{request.NodeId}' ({message.StopDetails?.Category}).");
        }

        if (message.StopReason == "max_tokens")
        {
            throw new InvalidOperationException($"Response for '{request.NodeId}' was truncated at max_tokens; the change is too large for one step.");
        }

        var text = string.Concat(message.Content.Select(b => b.TryPickText(out var t) ? t.Text : string.Empty));
        return new ChatResponse(text, model, message.Usage.InputTokens + (message.Usage.CacheReadInputTokens ?? 0) + (message.Usage.CacheCreationInputTokens ?? 0), message.Usage.OutputTokens);
    }
}

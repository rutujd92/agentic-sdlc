using System.Collections.Concurrent;
using Orchestrator.Core.Agents;

namespace Orchestrator.Tests.Fakes;

/// <summary>Returns canned JSON per node and records every request.</summary>
public sealed class FakeChatProvider(Dictionary<string, string> responses) : IChatProvider
{
    public ConcurrentQueue<ChatRequest> Requests { get; } = new();

    public Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        Requests.Enqueue(request);
        return Task.FromResult(new ChatResponse(responses[request.NodeId], "fake-model", 100, 20));
    }
}

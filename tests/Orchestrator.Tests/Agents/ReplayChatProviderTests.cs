using Orchestrator.Core.Agents;
using Orchestrator.Infrastructure.Agents;
using Orchestrator.Tests.Fakes;

namespace Orchestrator.Tests.Agents;

public sealed class ReplayChatProviderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"orch-replay-{Guid.NewGuid():N}");
    private readonly CancellationToken _ct = CancellationToken.None;

    private static ChatRequest Request(string node, string? feedback = null) =>
        new("run", node, "system", "context", "do it", "{}", AgentEffort.Medium) { Feedback = feedback };

    [Fact]
    public async Task Recording_then_replay_returns_identical_responses_keyed_by_node_and_feedback()
    {
        var live = new FakeChatProvider(new() { ["design"] = "{\"v\":1}" });
        var recorder = new RecordingChatProvider(live, _dir);
        await recorder.CompleteAsync(Request("design"), _ct);
        live = new FakeChatProvider(new() { ["design"] = "{\"v\":2}" });
        await new RecordingChatProvider(live, _dir).CompleteAsync(Request("design", "use a denylist"), _ct);

        var replay = new ReplayChatProvider(_dir);

        Assert.Equal("{\"v\":1}", (await replay.CompleteAsync(Request("design"), _ct)).Json);
        Assert.Equal("{\"v\":2}", (await replay.CompleteAsync(Request("design", "use a denylist"), _ct)).Json);
        Assert.Equal("replay:fake-model", (await replay.CompleteAsync(Request("design"), _ct)).Model);
    }

    [Fact]
    public async Task Replay_without_recording_fails_with_actionable_message()
    {
        var ex = await Assert.ThrowsAsync<FileNotFoundException>(() => new ReplayChatProvider(_dir).CompleteAsync(Request("design"), _ct));

        Assert.Contains("--live --record", ex.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}

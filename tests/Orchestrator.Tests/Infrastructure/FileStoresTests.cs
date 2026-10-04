using Orchestrator.Core.Events;
using Orchestrator.Core.Execution;
using Orchestrator.Infrastructure.Storage;

namespace Orchestrator.Tests.Infrastructure;

public sealed class FileStoresTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"orch-tests-{Guid.NewGuid():N}");
    private readonly CancellationToken _ct = CancellationToken.None;

    [Fact]
    public async Task JsonlEventStore_round_trips_events_in_order()
    {
        var store = new JsonlEventStore(_root);
        var first = new RunEvent(1, "r1", RunEventType.RunStarted, DateTimeOffset.UnixEpoch, "system") { Message = "cr", Data = new Dictionary<string, string> { ["nodes"] = "a,b" } };
        var second = new RunEvent(2, "r1", RunEventType.NodeStarted, DateTimeOffset.UnixEpoch.AddSeconds(1), "system") { NodeId = "a" };

        await store.AppendAsync(first, _ct);
        await store.AppendAsync(second, _ct);
        var events = await store.ReadAsync("r1", _ct);

        Assert.Equal(2, events.Count);
        Assert.Equal(RunEventType.RunStarted, events[0].Type);
        Assert.Equal("a,b", events[0].Data["nodes"]);
        Assert.Equal("a", events[1].NodeId);
        Assert.True(File.Exists(Path.Combine(_root, "r1", "events.jsonl")));
        Assert.Contains("\"type\":\"NodeStarted\"", (await File.ReadAllLinesAsync(Path.Combine(_root, "r1", "events.jsonl"), _ct))[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task JsonlEventStore_returns_empty_for_unknown_run()
    {
        Assert.Empty(await new JsonlEventStore(_root).ReadAsync("missing", _ct));
    }

    [Fact]
    public async Task FileArtifactStore_round_trips_artifacts()
    {
        var store = new FileArtifactStore(_root);
        var artifact = Artifact.Create("design", "# Design\nline 2");

        await store.SaveAsync("r1", artifact, _ct);

        Assert.Equal(artifact, await store.GetAsync("r1", "design", _ct));
        Assert.Null(await store.GetAsync("r1", "missing", _ct));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}

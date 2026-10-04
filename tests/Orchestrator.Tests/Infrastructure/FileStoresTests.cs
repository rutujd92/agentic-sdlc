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

public class RepositoryPolicyFileTests
{
    [Fact]
    public async Task Repository_policies_json_loads_and_protects_ci_and_itself()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(directory!.FullName, "policies.json")))
        {
            directory = directory.Parent;
        }

        var rules = await PolicyFile.LoadAsync(Path.Combine(directory.FullName, "policies.json"), CancellationToken.None);
        var engine = new Orchestrator.Core.Governance.PolicyEngine(rules);

        Assert.NotEmpty(rules.SecretPatterns);
        Assert.Equal(Orchestrator.Core.Governance.PolicyOutcome.Block, engine.Evaluate("diff --git a/.github/workflows/ci.yml b/.github/workflows/ci.yml\n+x\n").Outcome);
        Assert.Equal(Orchestrator.Core.Governance.PolicyOutcome.Block, engine.Evaluate("diff --git a/policies.json b/policies.json\n+{}\n").Outcome);
        Assert.Equal(Orchestrator.Core.Governance.PolicyOutcome.Allow, engine.Evaluate("diff --git a/src/UrlShortener.Api/Program.cs b/src/UrlShortener.Api/Program.cs\n+// ok\n").Outcome);
    }
}

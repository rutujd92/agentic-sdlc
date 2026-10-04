using Orchestrator.Core.Graph;

namespace Orchestrator.Tests.Graph;

public class StandardSdlcGraphTests
{
    private readonly WorkflowGraph _graph = StandardSdlcGraph.Create();

    [Fact]
    public void Starts_with_requirements_and_ends_with_merge()
    {
        Assert.Equal(StandardSdlcGraph.Requirements, _graph.Entry.Id);
        Assert.Equal(StandardSdlcGraph.Merge, _graph.TopologicalOrder[^1].Id);
    }

    [Fact]
    public void Implement_tests_and_docs_run_in_parallel_and_join_at_validate()
    {
        var parallel = _graph.Layers.Single(l => l.Any(n => n.Id == StandardSdlcGraph.Implement));

        Assert.Equal(
            [StandardSdlcGraph.Implement, StandardSdlcGraph.Tests, StandardSdlcGraph.Docs],
            parallel.Select(n => n.Id));
        Assert.Equal(
            [StandardSdlcGraph.Implement, StandardSdlcGraph.Tests, StandardSdlcGraph.Docs],
            _graph.Get(StandardSdlcGraph.Validate).DependsOn);
    }

    [Fact]
    public void Merge_requires_human_approval_and_is_high_risk()
    {
        var merge = _graph.Get(StandardSdlcGraph.Merge);

        Assert.True(merge.RequiresApproval);
        Assert.Equal(RiskLevel.High, merge.Risk);
    }

    [Fact]
    public void Covers_every_sdlc_stage()
    {
        Assert.Equal(
            [NodeKind.Requirements, NodeKind.Design, NodeKind.Implement, NodeKind.Tests, NodeKind.Docs, NodeKind.Validate, NodeKind.ReleaseReadiness, NodeKind.Merge],
            _graph.TopologicalOrder.Select(n => n.Kind));
    }
}

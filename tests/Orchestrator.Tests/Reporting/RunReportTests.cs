using Orchestrator.Core.Events;
using Orchestrator.Core.Execution;
using Orchestrator.Infrastructure.Reporting;

namespace Orchestrator.Tests.Reporting;

public class RunReportTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    private static List<RunEvent> Events() =>
    [
        new(1, "r1", RunEventType.RunStarted, T0, "system") { Message = "Add <b>QR</b>", Data = new Dictionary<string, string> { ["nodes"] = "design,implement,migration", ["edges"] = "design->implement" } },
        new(2, "r1", RunEventType.NodeStarted, T0.AddSeconds(1), "system") { NodeId = "design" },
        new(3, "r1", RunEventType.Replanned, T0.AddSeconds(2), "agent:design") { NodeId = "design", Message = "needs migration", Data = new Dictionary<string, string> { ["addedNodes"] = "migration", ["addedDependencies"] = "implement->migration" } },
        new(4, "r1", RunEventType.NodeSucceeded, T0.AddSeconds(2), "agent:design") { NodeId = "design", Data = new Dictionary<string, string> { ["outputHash"] = "abc" } },
        new(5, "r1", RunEventType.PolicyViolation, T0.AddSeconds(3), "system") { NodeId = "implement", Message = "[Block] secrets: Possible secret" },
        new(6, "r1", RunEventType.ApprovalRequested, T0.AddSeconds(4), "system") { NodeId = "migration", Data = new Dictionary<string, string> { ["phase"] = "pre" } },
        new(7, "r1", RunEventType.RunPaused, T0.AddSeconds(4), "system") { Message = "Awaiting human approval: migration." },
    ];

    private static readonly Dictionary<string, Artifact> Artifacts = new() { ["design"] = Artifact.Create("design", "<script>alert(1)</script>") };

    [Fact]
    public void Report_contains_dag_with_original_and_replanned_edges_and_statuses()
    {
        var html = RunReport.Render(Events(), Artifacts);

        Assert.Contains("design --> implement", html, StringComparison.Ordinal);
        Assert.Contains("migration --> implement", html, StringComparison.Ordinal);
        Assert.Contains("class design succeeded", html, StringComparison.Ordinal);
        Assert.Contains("class migration awaiting", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Replanned_node_edges_come_from_the_recorded_plan()
    {
        var change = new Orchestrator.Core.Graph.GraphChange(
            [new Orchestrator.Core.Graph.WorkflowNode("migration", Orchestrator.Core.Graph.NodeKind.Migration) { DependsOn = ["implement"] }], [], "r");
        var events = Events();
        events[2] = events[2] with { Data = new Dictionary<string, string> { ["addedNodes"] = "migration", ["addedDependencies"] = "", ["change"] = change.ToJson() } };

        var html = RunReport.Render(events, Artifacts);

        Assert.Contains("implement --> migration", html, StringComparison.Ordinal);
        Assert.DoesNotContain("design --> migration", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Report_encodes_all_run_content()
    {
        var html = RunReport.Render(Events(), Artifacts);

        Assert.DoesNotContain("<script>alert(1)</script>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>QR</b>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Report_lists_policy_findings_and_pending_decisions()
    {
        var html = RunReport.Render(Events(), Artifacts);

        Assert.Contains("[Block] secrets: Possible secret", html, StringComparison.Ordinal);
        Assert.Contains("Awaiting human approval: migration.", html, StringComparison.Ordinal);
        Assert.Contains("Paused", html, StringComparison.Ordinal);
    }
}

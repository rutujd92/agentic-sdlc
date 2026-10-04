using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Orchestrator.Core.Events;
using Orchestrator.Core.Execution;
using Orchestrator.Core.Graph;
using Orchestrator.Core.Metrics;
using Orchestrator.Core.State;

namespace Orchestrator.Infrastructure.Reporting;

/// <summary>
/// Self-contained HTML report for one run (the reviewer-facing view): DAG coloured by node status,
/// reliability metrics, human decisions, policy findings, per-node stats, the audit timeline and artifacts.
/// Everything from the run is HTML-encoded; only the Mermaid renderer is loaded from a CDN.
/// </summary>
public static partial class RunReport
{
    public static string Render(IReadOnlyList<RunEvent> events, IReadOnlyDictionary<string, Artifact> artifacts)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(artifacts);
        var state = RunState.Rebuild(events);
        var metrics = RunMetrics.From(events);
        var start = events[0].Timestamp;

        var html = new StringBuilder();
        html.Append(CultureInfo.InvariantCulture, $"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Run {E(state.RunId)}</title>
            <style>{Stylesheet}</style></head><body><main>
            <h1>Run <code>{E(state.RunId)}</code> <span class="badge {state.Status.ToString().ToLowerInvariant()}">{state.Status}</span></h1>
            <p class="cr">{E(state.ChangeRequest)}</p>
            """);

        html.Append("<section class=\"tiles\">")
            .Append(Tile("End-to-end", Duration(metrics.EndToEndLatency)))
            .Append(Tile("Active", Duration(metrics.ActiveTime)))
            .Append(Tile("Human wait", Duration(metrics.HumanWaitTime)))
            .Append(Tile("Attempts", metrics.Attempts.ToString(CultureInfo.InvariantCulture)))
            .Append(Tile("Retry rate", metrics.RetryRate.ToString("P0", CultureInfo.InvariantCulture)))
            .Append(Tile("MTTR", metrics.MeanTimeToRecovery is { } mttr ? Duration(mttr) : "–"))
            .Append(Tile("Rollbacks", metrics.Rollbacks.ToString(CultureInfo.InvariantCulture)))
            .Append(Tile("Policy findings", metrics.PolicyViolations.ToString(CultureInfo.InvariantCulture)))
            .Append(Tile("Tokens in / out", $"{metrics.InputTokens:N0} / {metrics.OutputTokens:N0}"))
            .Append("</section>");

        html.Append("<h2>Dependency graph</h2><pre class=\"mermaid\">").Append(Mermaid(events, state)).Append("</pre>");

        var decisions = events.Where(e => e.Type is RunEventType.ApprovalRequested or RunEventType.ApprovalGranted or RunEventType.ApprovalRejected
            or RunEventType.RunPaused or RunEventType.SafeStopped or RunEventType.Replanned or RunEventType.RolledBack
            || e.Actor.StartsWith("human:", StringComparison.Ordinal)).ToArray();
        html.Append("<h2>Decisions and governance</h2>").Append(Table(decisions, start));
        html.Append("<h2>Policy findings</h2>").Append(Table(events.Where(e => e.Type == RunEventType.PolicyViolation).ToArray(), start));

        html.Append("<h2>Nodes</h2><table><tr><th>Node</th><th>Status</th><th>Attempts</th><th>Latency</th><th>Actor</th><th>Model</th><th>Tokens in/out</th><th>Output</th></tr>");
        foreach (var (id, node) in state.Nodes)
        {
            var m = metrics.Nodes.GetValueOrDefault(id);
            html.Append(CultureInfo.InvariantCulture,
                $"<tr><td>{E(id)}</td><td><span class=\"badge {Css(node.Status)}\">{node.Status}</span></td><td>{node.Attempts}</td>" +
                $"<td>{(m?.Latency is { } l ? Duration(l) : "–")}</td><td>{E(m?.Actor ?? "–")}</td><td>{E(m?.Model ?? "–")}</td>" +
                $"<td>{m?.InputTokens ?? 0:N0} / {m?.OutputTokens ?? 0:N0}</td><td><code>{E(node.OutputHash?[..Math.Min(12, node.OutputHash.Length)] ?? "–")}</code></td></tr>");
        }

        html.Append("</table><h2>Audit timeline</h2>").Append(Table(events, start));

        html.Append("<h2>Artifacts</h2>");
        foreach (var (id, artifact) in artifacts.OrderBy(a => a.Key, StringComparer.Ordinal))
        {
            html.Append(CultureInfo.InvariantCulture, $"<details><summary>{E(id)} <code>{E(artifact.Hash[..12])}</code></summary><pre>{E(artifact.Content)}</pre></details>");
        }

        html.Append("""
            </main>
            <script src="https://cdn.jsdelivr.net/npm/mermaid@11/dist/mermaid.min.js"></script>
            <script>if (window.mermaid) { mermaid.initialize({ startOnLoad: true, theme: matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'default' }); }</script>
            </body></html>
            """);
        return html.ToString();
    }

    private static string Mermaid(IReadOnlyList<RunEvent> events, RunState state)
    {
        var edges = new List<string>();
        foreach (var e in events)
        {
            var list = e.Type switch
            {
                RunEventType.RunStarted => e.Data.GetValueOrDefault("edges"),
                RunEventType.Replanned => e.Data.GetValueOrDefault("addedDependencies") is { Length: > 0 } added
                    ? string.Join(';', added.Split(';').Select(Reverse))
                    : null,
                _ => null,
            };
            edges.AddRange((list ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries));
        }

        // Added nodes' own dependencies come from the recorded plan (fallback: the node that re-planned).
        foreach (var e in events.Where(e => e.Type == RunEventType.Replanned && e.NodeId is not null))
        {
            if (e.Data.TryGetValue("change", out var json))
            {
                edges.AddRange(GraphChange.FromJson(json).AddedNodes.SelectMany(n => n.DependsOn.Select(d => $"{d}->{n.Id}")));
                continue;
            }

            foreach (var added in e.Data.GetValueOrDefault("addedNodes", string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                edges.Add($"{e.NodeId}->{added}");
            }
        }

        var mermaid = new StringBuilder("flowchart LR\n");
        foreach (var (id, node) in state.Nodes)
        {
            mermaid.Append(CultureInfo.InvariantCulture, $"  {Id(id)}[\"{Id(id)}<br/>{node.Status} · {node.Attempts} attempt(s)\"]\n");
        }

        foreach (var edge in edges.Distinct(StringComparer.Ordinal))
        {
            var parts = edge.Split("->");
            mermaid.Append(CultureInfo.InvariantCulture, $"  {Id(parts[0])} --> {Id(parts[1])}\n");
        }

        foreach (var (id, node) in state.Nodes)
        {
            mermaid.Append(CultureInfo.InvariantCulture, $"  class {Id(id)} {Css(node.Status)}\n");
        }

        return mermaid.Append("""
              classDef succeeded fill:#d3f9d8,stroke:#2b8a3e,color:#111
              classDef failed fill:#ffe3e3,stroke:#c92a2a,color:#111
              classDef awaiting fill:#fff3bf,stroke:#e67700,color:#111
              classDef skipped fill:#e9ecef,stroke:#868e96,color:#111
              classDef pending fill:#f1f3f5,stroke:#adb5bd,color:#111
              classDef invalidated fill:#e5dbff,stroke:#6741d9,color:#111
            """).ToString();

        // "implement->migration" in addedDependencies means implement depends on migration: edge migration --> implement.
        static string Reverse(string dependency) => string.Join("->", dependency.Split("->").Reverse());
    }

    private static string Table(IReadOnlyList<RunEvent> events, DateTimeOffset start)
    {
        if (events.Count == 0)
        {
            return "<p class=\"muted\">None.</p>";
        }

        var table = new StringBuilder("<table><tr><th>#</th><th>+s</th><th>Actor</th><th>Event</th><th>Node</th><th>Detail</th></tr>");
        foreach (var e in events)
        {
            var data = string.Join(" ", e.Data.Where(d => d.Key is not ("change" or "nodes" or "edges" or "inputHashes")).Select(d => $"{d.Key}={d.Value}"));
            table.Append(CultureInfo.InvariantCulture,
                $"<tr><td>{e.Sequence}</td><td>{(e.Timestamp - start).TotalSeconds:0.0}</td><td>{E(e.Actor)}</td><td>{e.Type}</td>" +
                $"<td>{E(e.NodeId ?? string.Empty)}</td><td>{E(e.Message ?? string.Empty)} <span class=\"muted\">{E(data)}</span></td></tr>");
        }

        return table.Append("</table>").ToString();
    }

    private static string Tile(string label, string value) => $"<div class=\"tile\"><div class=\"muted\">{E(label)}</div><div class=\"value\">{E(value)}</div></div>";

    private static string Duration(TimeSpan d) => d.TotalSeconds < 120
        ? d.TotalSeconds.ToString("0.0s", CultureInfo.InvariantCulture)
        : d.TotalMinutes.ToString("0.0m", CultureInfo.InvariantCulture);

    private static string Css(NodeStatus status) => status switch
    {
        NodeStatus.Succeeded => "succeeded",
        NodeStatus.Failed => "failed",
        NodeStatus.AwaitingApproval => "awaiting",
        NodeStatus.Skipped => "skipped",
        NodeStatus.Invalidated => "invalidated",
        _ => "pending",
    };

    private static string Id(string id) => SafeId().Replace(id, "_");

    private static string E(string value) => WebUtility.HtmlEncode(value);

    [GeneratedRegex("[^A-Za-z0-9_-]")]
    private static partial Regex SafeId();

    private static string Stylesheet => """
        :root { --bg:#ffffff; --fg:#1b1f24; --muted:#6a737d; --line:#e1e4e8; --panel:#f6f8fa; }
        @media (prefers-color-scheme: dark) { :root { --bg:#0f1115; --fg:#e6e6e6; --muted:#9aa4af; --line:#2a2f36; --panel:#171a20; } }
        body { margin:0; background:var(--bg); color:var(--fg); font:14px/1.5 system-ui, sans-serif; }
        main { max-width:1100px; margin:0 auto; padding:24px 16px; }
        h1 { font-size:22px; } h2 { font-size:16px; margin-top:32px; border-bottom:1px solid var(--line); padding-bottom:4px; }
        .cr { font-size:15px; color:var(--muted); }
        .tiles { display:grid; grid-template-columns:repeat(auto-fill,minmax(150px,1fr)); gap:8px; }
        .tile { background:var(--panel); border:1px solid var(--line); border-radius:8px; padding:10px; }
        .tile .value { font-size:18px; font-weight:600; }
        table { width:100%; border-collapse:collapse; font-size:13px; display:block; overflow-x:auto; }
        th, td { text-align:left; padding:4px 8px; border-bottom:1px solid var(--line); vertical-align:top; }
        pre { background:var(--panel); border:1px solid var(--line); border-radius:6px; padding:10px; overflow-x:auto; font-size:12px; }
        .muted { color:var(--muted); } code { font-size:12px; }
        .badge { border-radius:10px; padding:1px 8px; font-size:12px; border:1px solid var(--line); }
        .succeeded { background:#d3f9d8; color:#1b4d2a; } .failed, .rolledback { background:#ffe3e3; color:#7a1f1f; }
        .awaiting, .paused { background:#fff3bf; color:#6b4a00; } .stopped, .skipped, .pending, .running { background:#e9ecef; color:#343a40; }
        .invalidated { background:#e5dbff; color:#3b1f8a; }
        details { margin:6px 0; } summary { cursor:pointer; }
        """;
}

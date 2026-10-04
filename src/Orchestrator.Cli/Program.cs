using System.Globalization;
using Orchestrator.Core.Events;
using Orchestrator.Core.Execution;
using Orchestrator.Core.Graph;
using Orchestrator.Core.State;
using Orchestrator.Infrastructure.Agents;
using Orchestrator.Infrastructure.Storage;

var runsRoot = Path.Combine(Directory.GetCurrentDirectory(), "runs");

switch (args.FirstOrDefault())
{
    case "graph":
        PrintGraph(StandardSdlcGraph.Create());
        return 0;
    case "run" when args.Length >= 2:
        return await RunAsync(args[1], OptionValues(args, "--fail"));
    case "status" when args.Length >= 2:
        return await StatusAsync(args[1]);
    default:
        Console.WriteLine("Usage: orchestrator <command>");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  graph                              Show the standard SDLC graph and its parallel layers");
        Console.WriteLine("  run \"<change request>\" [--fail id]  Run the graph with simulated agents (fail a node to see skipping)");
        Console.WriteLine("  status <runId>                     Rebuild and show a run's state from its event log");
        return args.Length == 0 ? 0 : 1;
}

async Task<int> RunAsync(string changeRequest, IReadOnlySet<string> failing)
{
    var runId = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..4]}";
    var started = DateTimeOffset.UtcNow;
    var engine = new OrchestrationEngine(
        new JsonlEventStore(runsRoot),
        new FileArtifactStore(runsRoot),
        TimeProvider.System,
        new EngineOptions { OnEvent = e => PrintEvent(e, started) });

    Console.WriteLine($"Run {runId}");
    Console.WriteLine($"Change request: {changeRequest}");
    Console.WriteLine();

    var executor = new SimulatedExecutor(TimeSpan.FromMilliseconds(400), failing);
    var state = await engine.RunAsync(runId, changeRequest, new WorkflowDefinition(StandardSdlcGraph.Create(), _ => executor), CancellationToken.None);

    Console.WriteLine();
    Console.WriteLine($"Result: {state.Status}  ({(DateTimeOffset.UtcNow - started).TotalSeconds:0.0}s)");
    Console.WriteLine($"Event log: {Path.Combine(runsRoot, runId, "events.jsonl")}");
    return state.Status == RunStatus.Succeeded ? 0 : 2;
}

async Task<int> StatusAsync(string runId)
{
    var events = await new JsonlEventStore(runsRoot).ReadAsync(runId, CancellationToken.None);
    if (events.Count == 0)
    {
        Console.Error.WriteLine($"No events found for run '{runId}' in {runsRoot}.");
        return 1;
    }

    var state = RunState.Rebuild(events);
    Console.WriteLine($"Run {state.RunId}: {state.Status}  ({events.Count} events)");
    Console.WriteLine($"Change request: {state.ChangeRequest}");
    foreach (var (id, node) in state.Nodes)
    {
        var hash = node.OutputHash is null ? string.Empty : $"  output={node.OutputHash[..12]}";
        var error = node.Error is null ? string.Empty : $"  {node.Error}";
        Console.WriteLine($"  {id,-18} {node.Status,-10} attempts={node.Attempts}{hash}{error}");
    }

    return 0;
}

static void PrintEvent(RunEvent e, DateTimeOffset started)
{
    if (e.Type is RunEventType.NodeReady or RunEventType.GatePassed)
    {
        return;
    }

    var elapsed = (e.Timestamp - started).TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture).PadLeft(6);
    var marker = e.Type switch
    {
        RunEventType.NodeStarted => "▶",
        RunEventType.NodeSucceeded => "✔",
        RunEventType.NodeFailed or RunEventType.GateFailed or RunEventType.RunFailed => "✖",
        RunEventType.NodeSkipped => "⤼",
        _ => "•",
    };
    var node = e.NodeId is null ? string.Empty : $"{e.NodeId,-18}";
    var detail = e.Type == RunEventType.RunStarted ? string.Empty : e.Message ?? string.Empty;
    Console.WriteLine($"{elapsed}s  #{e.Sequence,-3} {marker} {e.Type,-14} {node} {detail}".TrimEnd());
}

static HashSet<string> OptionValues(string[] args, string option) =>
    args.Select((a, i) => (a, i)).Where(x => x.a == option && x.i + 1 < args.Length).Select(x => args[x.i + 1]).ToHashSet(StringComparer.Ordinal);

static void PrintGraph(WorkflowGraph graph)
{
    for (var i = 0; i < graph.Layers.Count; i++)
    {
        var layer = graph.Layers[i];
        var label = layer.Count > 1 ? "  (parallel)" : string.Empty;
        Console.WriteLine($"Layer {i + 1}{label}");
        foreach (var node in layer)
        {
            var approval = node.RequiresApproval ? "  [human approval]" : string.Empty;
            var after = node.DependsOn.Count > 0 ? $"  after: {string.Join(", ", node.DependsOn)}" : string.Empty;
            Console.WriteLine($"  - {node.Id,-18} risk={node.Risk,-6}{approval}{after}");
        }
    }
}

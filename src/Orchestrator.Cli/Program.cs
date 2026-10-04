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
        return await RunAsync(args[1], OptionValues(args, "--fail"), OptionValues(args, "--flaky"), DelayOption(args));
    case "resume" when args.Length >= 2:
        return await ResumeAsync(args[1], DelayOption(args));
    case "stop" when args.Length >= 2:
        Directory.CreateDirectory(Path.Combine(runsRoot, args[1]));
        await File.WriteAllTextAsync(FileStopSignal.StopFile(runsRoot, args[1]), $"Stop requested at {DateTimeOffset.UtcNow:O}\n");
        Console.WriteLine($"Stop requested for {args[1]}; it halts at the next node boundary.");
        return 0;
    case "status" when args.Length >= 2:
        return await StatusAsync(args[1]);
    default:
        Console.WriteLine("Usage: orchestrator <command>");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  graph                              Show the standard SDLC graph and its parallel layers");
        Console.WriteLine("  run \"<change request>\" [options]    Run the SDLC graph with simulated agents");
        Console.WriteLine("      --fail <node>                  Node always fails (retries, fallback, then skip downstream)");
        Console.WriteLine("      --flaky <node>                 Node fails on its first attempt only (shows retry)");
        Console.WriteLine("      --delay <ms>                   Simulated work per node (default 400)");
        Console.WriteLine("  stop <runId>                       Safe-stop a running run at the next node boundary");
        Console.WriteLine("  resume <runId> [--delay <ms>]      Continue a stopped run from its event log");
        Console.WriteLine("  status <runId>                     Rebuild and show a run's state from its event log");
        return args.Length == 0 ? 0 : 1;
}

async Task<int> RunAsync(string changeRequest, IReadOnlySet<string> failing, IReadOnlySet<string> flaky, TimeSpan delay)
{
    var runId = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..4]}";
    Console.WriteLine($"Run {runId}");
    Console.WriteLine($"Change request: {changeRequest}");
    Console.WriteLine($"(stop it from another terminal with: dotnet run --project src/Orchestrator.Cli -- stop {runId})");
    Console.WriteLine();

    var started = DateTimeOffset.UtcNow;
    var state = await CreateEngine(started).RunAsync(runId, changeRequest, SimulatedWorkflow(delay, failing, flaky), CancellationToken.None);
    return Summarize(runId, state, started);
}

async Task<int> ResumeAsync(string runId, TimeSpan delay)
{
    var stopFile = FileStopSignal.StopFile(runsRoot, runId);
    if (File.Exists(stopFile))
    {
        File.Delete(stopFile);
    }

    Console.WriteLine($"Resuming {runId}");
    Console.WriteLine();
    var started = DateTimeOffset.UtcNow;
    var state = await CreateEngine(started).ResumeAsync(runId, SimulatedWorkflow(delay, new HashSet<string>(), new HashSet<string>()), CancellationToken.None);
    return Summarize(runId, state, started);
}

OrchestrationEngine CreateEngine(DateTimeOffset started) => new(
    new JsonlEventStore(runsRoot),
    new FileArtifactStore(runsRoot),
    TimeProvider.System,
    new EngineOptions { OnEvent = e => PrintEvent(e, started) },
    checkpointStore: null,
    new FileStopSignal(runsRoot));

static WorkflowDefinition SimulatedWorkflow(TimeSpan delay, IReadOnlySet<string> failing, IReadOnlySet<string> flaky)
{
    var primary = new SimulatedExecutor(delay, failing, flaky);
    var fallback = new SimulatedExecutor(delay, failing);
    return new WorkflowDefinition(StandardSdlcGraph.Create(), _ => primary)
    {
        RetryPolicyFor = _ => new RetryPolicy(MaxAttempts: 2, InitialBackoff: TimeSpan.FromMilliseconds(250)),
        FallbacksFor = _ => [fallback],
    };
}

int Summarize(string runId, RunState state, DateTimeOffset started)
{
    Console.WriteLine();
    Console.WriteLine($"Result: {state.Status}  ({(DateTimeOffset.UtcNow - started).TotalSeconds:0.0}s)");
    Console.WriteLine($"Event log: {Path.Combine(runsRoot, runId, "events.jsonl")}");
    if (state.Status == RunStatus.Stopped)
    {
        Console.WriteLine($"Resume with: dotnet run --project src/Orchestrator.Cli -- resume {runId}");
    }

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
        RunEventType.RetryScheduled => "↻",
        RunEventType.FallbackUsed => "⇄",
        RunEventType.SafeStopped => "■",
        RunEventType.RunResumed => "▷",
        RunEventType.RolledBack => "⟲",
        _ => "•",
    };
    var node = e.NodeId is null ? string.Empty : $"{e.NodeId,-18}";
    var detail = e.Type == RunEventType.RunStarted ? string.Empty : e.Message ?? string.Empty;
    if (e.Type == RunEventType.RetryScheduled)
    {
        detail = $"retry in {e.Data["delayMs"]}ms (attempt {e.Data["nextAttempt"]}): {detail}";
    }

    Console.WriteLine($"{elapsed}s  #{e.Sequence,-3} {marker} {e.Type,-14} {node} {detail}".TrimEnd());
}

static TimeSpan DelayOption(string[] args) =>
    TimeSpan.FromMilliseconds(OptionValues(args, "--delay").Select(v => int.Parse(v, CultureInfo.InvariantCulture)).DefaultIfEmpty(400).First());

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

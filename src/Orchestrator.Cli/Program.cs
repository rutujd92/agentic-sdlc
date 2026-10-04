using System.Globalization;
using Orchestrator.Core.Events;
using Orchestrator.Core.Execution;
using Orchestrator.Core.Governance;
using Orchestrator.Core.Graph;
using Orchestrator.Core.State;
using Orchestrator.Infrastructure.Agents;
using Orchestrator.Infrastructure.Storage;
using Orchestrator.Infrastructure.Workspace;

var runsRoot = Path.Combine(Directory.GetCurrentDirectory(), "runs");
var policyPath = Path.Combine(Directory.GetCurrentDirectory(), "policies.json");

switch (args.FirstOrDefault())
{
    case "graph":
        PrintGraph(StandardSdlcGraph.Create());
        return 0;
    case "run" when args.Length >= 2:
        return await RunAsync(args[1], OptionValues(args, "--fail"), OptionValues(args, "--flaky"), OptionValues(args, "--with"), DelayOption(args));
    case "approve" when args.Length >= 3:
        return await DecideAsync(args[1], args[2], approve: true, OptionValues(args, "--note").FirstOrDefault());
    case "reject" when args.Length >= 3:
        return await DecideAsync(args[1], args[2], approve: false, OptionValues(args, "--reason").FirstOrDefault());
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
        Console.WriteLine("      --with migration|package|secret  Add a policy-relevant change to the implement diff");
        Console.WriteLine("  approve <runId> <node> [--note t]  Approve a paused node (as git user.email) and resume");
        Console.WriteLine("  reject <runId> <node> --reason t   Reject a paused node and resume (fails it, rolls back)");
        Console.WriteLine("  stop <runId>                       Safe-stop a running run at the next node boundary");
        Console.WriteLine("  resume <runId> [--delay <ms>]      Continue a stopped run from its event log");
        Console.WriteLine("  status <runId>                     Rebuild and show a run's state from its event log");
        return args.Length == 0 ? 0 : 1;
}

async Task<int> RunAsync(string changeRequest, IReadOnlySet<string> failing, IReadOnlySet<string> flaky, IReadOnlySet<string> extras, TimeSpan delay)
{
    var runId = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..4]}";
    Console.WriteLine($"Run {runId}");
    Console.WriteLine($"Change request: {changeRequest}");
    Console.WriteLine($"(stop it from another terminal with: dotnet run --project src/Orchestrator.Cli -- stop {runId})");
    Console.WriteLine();

    var started = DateTimeOffset.UtcNow;
    var state = await CreateEngine(started).RunAsync(runId, changeRequest, await SimulatedWorkflowAsync(delay, failing, flaky, extras), CancellationToken.None);
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
    var state = await CreateEngine(started).ResumeAsync(runId, await SimulatedWorkflowAsync(delay, Empty(), Empty(), Empty()), CancellationToken.None);
    return Summarize(runId, state, started);
}

async Task<int> DecideAsync(string runId, string nodeId, bool approve, string? text)
{
    if (!approve && string.IsNullOrWhiteSpace(text))
    {
        Console.Error.WriteLine("A rejection needs --reason \"...\".");
        return 1;
    }

    var approver = await GitUserEmailAsync();
    var approvals = new ApprovalService(new JsonlEventStore(runsRoot), TimeProvider.System);
    try
    {
        if (approve)
        {
            await approvals.GrantAsync(runId, nodeId, approver, text, CancellationToken.None);
        }
        else
        {
            await approvals.RejectAsync(runId, nodeId, approver, text!, CancellationToken.None);
        }
    }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }

    Console.WriteLine($"{(approve ? "Approved" : "Rejected")} '{nodeId}' as {approver}.");
    return await ResumeAsync(runId, TimeSpan.FromMilliseconds(400));
}

static async Task<string> GitUserEmailAsync()
{
    var result = await ProcessRunner.RunAsync("git", Directory.GetCurrentDirectory(), ["config", "user.email"], CancellationToken.None);
    var email = result.StandardOutput.Trim();
    return result.Succeeded && email.Length > 0 ? email : Environment.UserName;
}

static HashSet<string> Empty() => new(StringComparer.Ordinal);

OrchestrationEngine CreateEngine(DateTimeOffset started) => new(
    new JsonlEventStore(runsRoot),
    new FileArtifactStore(runsRoot),
    TimeProvider.System,
    new EngineOptions { OnEvent = e => PrintEvent(e, started) },
    checkpointStore: null,
    new FileStopSignal(runsRoot));

async Task<WorkflowDefinition> SimulatedWorkflowAsync(TimeSpan delay, IReadOnlySet<string> failing, IReadOnlySet<string> flaky, IReadOnlySet<string> extras)
{
    var policyGate = new PolicyGate(new PolicyEngine(await PolicyFile.LoadAsync(policyPath, CancellationToken.None)));
    var primary = new SimulatedExecutor(delay, failing, flaky, extras);
    var fallback = new SimulatedExecutor(delay, failing, implementExtras: extras);
    return new WorkflowDefinition(StandardSdlcGraph.Create(), _ => primary)
    {
        RetryPolicyFor = _ => new RetryPolicy(MaxAttempts: 2, InitialBackoff: TimeSpan.FromMilliseconds(250)),
        FallbacksFor = _ => [fallback],
        ExitGatesFor = n => n.Kind is NodeKind.Implement or NodeKind.Tests or NodeKind.Docs ? [policyGate] : [],
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

    foreach (var (node, _) in state.Nodes.Where(n => n.Value.Status == NodeStatus.AwaitingApproval))
    {
        Console.WriteLine($"Approve: dotnet run --project src/Orchestrator.Cli -- approve {runId} {node} --note \"...\"");
        Console.WriteLine($"Reject:  dotnet run --project src/Orchestrator.Cli -- reject {runId} {node} --reason \"...\"");
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
        RunEventType.ApprovalRequested or RunEventType.RunPaused => "⏸",
        RunEventType.ApprovalGranted => "✓",
        RunEventType.ApprovalRejected => "✗",
        RunEventType.PolicyViolation => "⚑",
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

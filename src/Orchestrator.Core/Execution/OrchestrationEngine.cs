using System.Collections.Concurrent;
using System.Globalization;
using Orchestrator.Core.Events;
using Orchestrator.Core.Graph;
using Orchestrator.Core.State;

namespace Orchestrator.Core.Execution;

/// <summary>
/// Executes a <see cref="WorkflowDefinition"/> as a dependency graph: every node whose dependencies
/// have succeeded starts immediately (bounded by <see cref="EngineOptions.MaxParallelism"/>), so
/// independent branches run in parallel and join nodes wait for all of their inputs.
/// Per node: entry gates, then attempts (bounded retries with backoff, then fallbacks), each attempt
/// checked by exit gates. Success creates a checkpoint; unrecoverable failure rolls back to the last one.
/// Operator stop and attempt/time budgets halt the run at a node boundary, leaving it resumable.
/// All state changes go through the append-only event log.
/// </summary>
public sealed class OrchestrationEngine(
    IEventStore eventStore,
    IArtifactStore artifactStore,
    TimeProvider timeProvider,
    EngineOptions? options = null,
    ICheckpointStore? checkpointStore = null,
    IStopSignal? stopSignal = null)
{
    private const string SystemActor = "system";
    private readonly EngineOptions _options = options ?? new EngineOptions();
    private readonly ICheckpointStore _checkpoints = checkpointStore ?? new NoCheckpoints();
    private readonly IStopSignal _stopSignal = stopSignal ?? new NeverStop();

    public async Task<RunState> RunAsync(string runId, string changeRequest, WorkflowDefinition workflow, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(workflow);

        using var run = new RunExecution(runId, eventStore, timeProvider, _options, new RunState(), workflow.Graph);
        await run.EmitAsync(RunEventType.RunStarted, null, SystemActor, changeRequest, cancellationToken,
            ("nodes", string.Join(',', workflow.Graph.TopologicalOrder.Select(n => n.Id))));

        return await ExecuteAsync(run, workflow, new ConcurrentDictionary<string, Artifact>(StringComparer.Ordinal), cancellationToken);
    }

    /// <summary>Continues a stopped or interrupted run from its event log; succeeded nodes are not re-executed.</summary>
    public async Task<RunState> ResumeAsync(string runId, WorkflowDefinition workflow, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        var events = await eventStore.ReadAsync(runId, cancellationToken);
        if (events.Count == 0)
        {
            throw new InvalidOperationException($"Run '{runId}' has no events.");
        }

        var state = RunState.Rebuild(events);
        if (state.Status is RunStatus.Succeeded or RunStatus.Failed or RunStatus.RolledBack)
        {
            throw new InvalidOperationException($"Run '{runId}' already finished with status {state.Status}.");
        }

        var outputs = new ConcurrentDictionary<string, Artifact>(StringComparer.Ordinal);
        foreach (var (id, node) in state.Nodes.Where(n => n.Value.Status == NodeStatus.Succeeded))
        {
            outputs[id] = await artifactStore.GetAsync(runId, id, cancellationToken)
                ?? throw new InvalidOperationException($"Artifact for succeeded node '{id}' is missing; cannot resume.");
        }

        // Re-apply recorded plan changes so the resumed run executes the same graph it was executing.
        var graph = events.Where(e => e.Type == RunEventType.Replanned)
            .Aggregate(workflow.Graph, (g, e) => g.Apply(GraphChange.FromJson(e.Data["change"])));
        using var run = new RunExecution(runId, eventStore, timeProvider, _options, state, graph);
        await run.EmitAsync(RunEventType.RunResumed, null, SystemActor, $"Resuming from event #{state.LastSequence}.", cancellationToken);
        return await ExecuteAsync(run, workflow, outputs, cancellationToken);
    }

    private async Task<RunState> ExecuteAsync(
        RunExecution run, WorkflowDefinition workflow, ConcurrentDictionary<string, Artifact> outputs, CancellationToken cancellationToken)
    {
        var startedAt = timeProvider.GetUtcNow();
        var running = new Dictionary<Task, string>();
        string? stopReason = null;

        while (true)
        {
            foreach (var candidate in run.Graph.TopologicalOrder)
            {
                // A node earlier in this pass may have re-planned (and can finish synchronously):
                // always evaluate the node's current definition, including any dependencies added by the plan.
                var node = run.Graph.Get(candidate.Id);

                // Checked before every node start (not once per pass): a node can finish synchronously
                // during this loop, and a stop must take effect at the very next node boundary.
                stopReason ??= StopReason(run, startedAt);
                if (stopReason is not null || running.Count >= _options.MaxParallelism)
                {
                    break;
                }

                var status = run.State.Nodes[node.Id];
                if (running.ContainsValue(node.Id))
                {
                    continue;
                }

                if (status is { Status: NodeStatus.AwaitingApproval, ApprovalPhase: "output", ApprovalGranted: true })
                {
                    running[CompleteApprovedOutputAsync(run, workflow, node, outputs, cancellationToken)] = node.Id;
                }

                // Ready/Running without an in-flight task means the node was interrupted (resume); run it again.
                else if (status.Status is NodeStatus.Pending or NodeStatus.Ready or NodeStatus.Running or NodeStatus.Invalidated
                    && node.DependsOn.All(d => run.State.Nodes[d].Status == NodeStatus.Succeeded))
                {
                    await run.EmitAsync(RunEventType.NodeReady, node.Id, SystemActor, null, cancellationToken);
                    running[RunNodeAsync(run, workflow, node, outputs, cancellationToken)] = node.Id;
                }
            }

            if (running.Count == 0)
            {
                break;
            }

            var finished = await Task.WhenAny(running.Keys);
            running.Remove(finished);
            await finished;
        }

        if (stopReason is not null)
        {
            await run.EmitAsync(RunEventType.SafeStopped, null, SystemActor, stopReason, cancellationToken);
            return run.State;
        }

        var awaiting = run.State.Nodes.Where(n => n.Value.Status == NodeStatus.AwaitingApproval).Select(n => n.Key).ToArray();
        if (awaiting.Length > 0)
        {
            await run.EmitAsync(RunEventType.RunPaused, null, SystemActor, $"Awaiting human approval: {string.Join(", ", awaiting)}.", cancellationToken);
            return run.State;
        }

        foreach (var node in run.Graph.TopologicalOrder.Where(n => run.State.Nodes[n.Id].Status is NodeStatus.Pending or NodeStatus.Invalidated))
        {
            var blockedBy = run.Graph.TransitiveDependencies(node.Id)
                .Where(d => run.State.Nodes[d.Id].Status == NodeStatus.Failed)
                .Select(d => d.Id);
            await run.EmitAsync(RunEventType.NodeSkipped, node.Id, SystemActor, $"Upstream failed: {string.Join(", ", blockedBy)}", cancellationToken);
        }

        if (run.State.Nodes.Values.All(n => n.Status == NodeStatus.Succeeded))
        {
            await run.EmitAsync(RunEventType.RunCompleted, null, SystemActor, "All nodes succeeded.", cancellationToken);
            return run.State;
        }

        await run.EmitAsync(RunEventType.RunFailed, null, SystemActor, "One or more nodes failed.", cancellationToken);
        if (run.State.LastCheckpointId is { } checkpoint)
        {
            await _checkpoints.RollbackAsync(run.RunId, checkpoint, cancellationToken);
            await run.EmitAsync(RunEventType.RolledBack, null, SystemActor, "Workspace restored to the last green checkpoint.", cancellationToken,
                ("checkpointId", checkpoint));
        }

        return run.State;
    }

    private string? StopReason(RunExecution run, DateTimeOffset startedAt)
    {
        if (_stopSignal.IsStopRequested(run.RunId))
        {
            return "Stop requested by operator.";
        }

        if (run.State.TotalAttempts >= _options.MaxTotalAttempts)
        {
            return $"Attempt budget exhausted ({run.State.TotalAttempts}/{_options.MaxTotalAttempts}).";
        }

        var elapsed = timeProvider.GetUtcNow() - startedAt;
        return elapsed > _options.MaxDuration ? $"Time budget exhausted ({elapsed:g} > {_options.MaxDuration:g})." : null;
    }

    private async Task RunNodeAsync(
        RunExecution run, WorkflowDefinition workflow, WorkflowNode node, ConcurrentDictionary<string, Artifact> outputs, CancellationToken cancellationToken)
    {
        var inputs = run.Graph.TransitiveDependencies(node.Id).ToDictionary(n => n.Id, n => outputs[n.Id], StringComparer.Ordinal);

        var (entryFailure, _) = await EvaluateGatesAsync(run, node, workflow.EntryGatesFor(node), "entry", new GateContext(run.RunId, node, inputs, null), cancellationToken);
        if (entryFailure is not null)
        {
            await run.EmitAsync(RunEventType.NodeFailed, node.Id, SystemActor, entryFailure, cancellationToken);
            return;
        }

        if (node.RequiresApproval && !run.State.Nodes[node.Id].ApprovalGranted)
        {
            await run.EmitAsync(RunEventType.ApprovalRequested, node.Id, SystemActor,
                $"'{node.Id}' is a {node.Risk}-risk action and needs human approval before it runs.", cancellationToken, ("phase", "pre"));
            return;
        }

        var policy = workflow.RetryPolicyFor(node);
        var executors = new List<(INodeExecutor Executor, int Attempts, string Label)> { (workflow.ExecutorFor(node), Math.Max(1, policy.MaxAttempts), "primary") };
        executors.AddRange(workflow.FallbacksFor(node).Select((f, i) => (f, 1, $"fallback-{i + 1}")));

        string? feedback = run.State.Nodes[node.Id].Guidance;
        foreach (var (executor, maxAttempts, label) in executors)
        {
            if (label != "primary")
            {
                await run.EmitAsync(RunEventType.FallbackUsed, node.Id, SystemActor, $"Primary exhausted; trying {label}. Last error: {feedback}", cancellationToken,
                    ("executor", label));
            }

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                var total = run.State.Nodes[node.Id].Attempts + 1;
                await run.EmitAsync(RunEventType.NodeStarted, node.Id, SystemActor, null, cancellationToken,
                    ("attempt", total.ToString(CultureInfo.InvariantCulture)), ("executor", label));

                var (output, actor, rationale, failure, approvalReasons, plan) = await AttemptAsync(run, workflow, node, executor, inputs, total, feedback, cancellationToken);
                if (failure is null && approvalReasons.Count > 0)
                {
                    await artifactStore.SaveAsync(run.RunId, output!, cancellationToken);
                    await run.EmitAsync(RunEventType.ApprovalRequested, node.Id, actor, string.Join(" ", approvalReasons), cancellationToken,
                        ("phase", "output"), ("outputHash", output!.Hash));
                    return;
                }

                if (failure is null)
                {
                    await CompleteAsync(run, node, inputs, output!, actor, rationale, outputs, cancellationToken, plan);
                    return;
                }

                feedback = failure;
                if (attempt < maxAttempts)
                {
                    var delay = policy.DelayBeforeAttempt(attempt + 1);
                    await run.EmitAsync(RunEventType.RetryScheduled, node.Id, SystemActor, failure, cancellationToken,
                        ("nextAttempt", (total + 1).ToString(CultureInfo.InvariantCulture)),
                        ("delayMs", ((long)delay.TotalMilliseconds).ToString(CultureInfo.InvariantCulture)));
                    await Task.Delay(delay, timeProvider, cancellationToken);
                }
            }
        }

        await run.EmitAsync(RunEventType.NodeFailed, node.Id, SystemActor, feedback, cancellationToken);
    }

    private static async Task<(Artifact? Output, string Actor, string? Rationale, string? Failure, IReadOnlyList<string> ApprovalReasons, GraphChange? Plan)> AttemptAsync(
        RunExecution run, WorkflowDefinition workflow, WorkflowNode node, INodeExecutor executor,
        IReadOnlyDictionary<string, Artifact> inputs, int attempt, string? feedback, CancellationToken cancellationToken)
    {
        NodeResult result;
        try
        {
            result = await executor.ExecuteAsync(
                new NodeExecutionContext(run.RunId, node, run.State.ChangeRequest, inputs, attempt) { Feedback = feedback }, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, SystemActor, null, $"Executor threw {ex.GetType().Name}: {ex.Message}", [], null);
        }

        if (!result.Succeeded || result.Output is null)
        {
            return (null, result.Actor, null, result.Error ?? "Executor returned no output.", [], null);
        }

        var (gateFailure, approvalReasons) = await EvaluateGatesAsync(run, node, workflow.ExitGatesFor(node), "exit", new GateContext(run.RunId, node, inputs, result.Output), cancellationToken);
        var (plan, planFailure) = ValidatePlan(run, node, result.Plan);
        return (result.Output, result.Actor, result.Rationale, gateFailure ?? planFailure, approvalReasons, plan);
    }

    private async Task CompleteAsync(
        RunExecution run, WorkflowNode node, IReadOnlyDictionary<string, Artifact> inputs, Artifact output, string actor, string? rationale,
        ConcurrentDictionary<string, Artifact> outputs, CancellationToken cancellationToken, GraphChange? plan = null)
    {
        var previousHash = run.State.Nodes[node.Id].OutputHash;
        await artifactStore.SaveAsync(run.RunId, output, cancellationToken);
        outputs[node.Id] = output;

        if (plan is { IsEmpty: false })
        {
            run.Graph = run.Graph.Apply(plan);
            await run.EmitAsync(RunEventType.Replanned, node.Id, actor, plan.Rationale, cancellationToken,
                ("addedNodes", string.Join(',', plan.AddedNodes.Select(n => n.Id))),
                ("addedDependencies", string.Join(';', plan.AddedDependencies.Select(e => $"{e.Node}->{e.DependsOn}"))),
                ("change", plan.ToJson()));
        }

        // Re-run (revision or upstream change) produced a different output: dependents that already used the old
        // output are stale. Only direct dependents are invalidated; they cascade further only if their output changes.
        // Invalidate BEFORE recording success: once this node is Succeeded the scheduler may start a dependent,
        // which must not then be invalidated by this same completion.
        if (previousHash is not null && previousHash != output.Hash)
        {
            foreach (var dependent in run.Graph.Dependents(node.Id))
            {
                // Status is checked under the event lock: parallel siblings finishing together must not
                // invalidate the same join node twice.
                await run.EmitIfAsync(
                    s => s.Nodes[dependent.Id].Status is not (NodeStatus.Pending or NodeStatus.Invalidated),
                    RunEventType.NodeInvalidated, dependent.Id, $"Input '{node.Id}' changed ({previousHash[..8]} -> {output.Hash[..8]}).",
                    [("reason", "upstream-changed")], cancellationToken);
            }
        }

        await run.EmitAsync(RunEventType.NodeSucceeded, node.Id, actor, rationale, cancellationToken,
            ("outputHash", output.Hash),
            ("inputHashes", string.Join(';', inputs.Values.Select(a => $"{a.NodeId}={a.Hash}"))));

        if (await _checkpoints.CreateAsync(run.RunId, $"after {node.Id}", cancellationToken) is { } checkpoint)
        {
            await run.EmitAsync(RunEventType.CheckpointCreated, node.Id, SystemActor, null, cancellationToken, ("checkpointId", checkpoint));
        }
    }

    /// <summary>
    /// Checks a proposed plan before it is accepted: governance applied (high-impact nodes need approval),
    /// already-present nodes/edges ignored (idempotent re-runs), nodes that already ran cannot be rewired,
    /// and the resulting graph must validate (no cycles, no unknown dependencies).
    /// </summary>
    private static (GraphChange? Plan, string? Failure) ValidatePlan(RunExecution run, WorkflowNode node, GraphChange? proposed)
    {
        if (proposed is null)
        {
            return (null, null);
        }

        var change = proposed.Governed() with
        {
            AddedNodes = proposed.Governed().AddedNodes.Where(n => !run.Graph.Contains(n.Id)).ToArray(),
            AddedDependencies = proposed.AddedDependencies
                .Where(e => !run.Graph.Contains(e.Node) || !run.Graph.Get(e.Node).DependsOn.Contains(e.DependsOn, StringComparer.Ordinal))
                .ToArray(),
        };

        var rewiredAfterRun = change.AddedDependencies
            .Where(e => run.Graph.Contains(e.Node) && run.State.Nodes[e.Node].Status is not (NodeStatus.Pending or NodeStatus.Invalidated))
            .Select(e => e.Node)
            .ToArray();
        if (rewiredAfterRun.Length > 0)
        {
            return (null, $"Proposed plan rewires node(s) that already ran: {string.Join(", ", rewiredAfterRun)}.");
        }

        try
        {
            run.Graph.Apply(change);
            return (change, null);
        }
        catch (InvalidWorkflowGraphException ex)
        {
            return (null, $"Proposed plan from '{node.Id}' is invalid: {string.Join(" ", ex.Errors)}");
        }
    }

    /// <returns>Failure is null when every gate passes; approval reasons list gates that passed but require human review.</returns>
    private static async Task<(string? Failure, IReadOnlyList<string> ApprovalReasons)> EvaluateGatesAsync(
        RunExecution run, WorkflowNode node, IReadOnlyList<IGate> gates, string phase, GateContext context, CancellationToken cancellationToken)
    {
        var approvalReasons = new List<string>();
        foreach (var gate in gates)
        {
            var result = await gate.EvaluateAsync(context, cancellationToken);
            foreach (var violation in result.Violations)
            {
                await run.EmitAsync(RunEventType.PolicyViolation, node.Id, SystemActor, violation, cancellationToken,
                    ("gate", gate.Name), ("outcome", result.Passed ? "RequireApproval" : "Block"));
            }

            await run.EmitAsync(result.Passed ? RunEventType.GatePassed : RunEventType.GateFailed, node.Id, SystemActor, result.Reason, cancellationToken,
                ("gate", gate.Name), ("phase", phase));
            if (!result.Passed)
            {
                return ($"{phase} gate '{gate.Name}' failed: {result.Reason}", approvalReasons);
            }

            if (result.RequiresApproval)
            {
                approvalReasons.Add(result.Reason);
            }
        }

        return (null, approvalReasons);
    }

    /// <summary>Completes a node whose output a human approved; the reviewed artifact is used as-is (no re-execution).</summary>
    private async Task CompleteApprovedOutputAsync(
        RunExecution run, WorkflowDefinition workflow, WorkflowNode node, ConcurrentDictionary<string, Artifact> outputs, CancellationToken cancellationToken)
    {
        var output = await artifactStore.GetAsync(run.RunId, node.Id, cancellationToken)
            ?? throw new InvalidOperationException($"Approved output for '{node.Id}' is missing.");
        if (output.Hash != run.State.Nodes[node.Id].PendingOutputHash)
        {
            throw new InvalidOperationException($"Approved output for '{node.Id}' changed after review (hash mismatch).");
        }

        var inputs = run.Graph.TransitiveDependencies(node.Id).ToDictionary(n => n.Id, n => outputs[n.Id], StringComparer.Ordinal);
        await CompleteAsync(run, node, inputs, output, "human-approved", "Output approved by reviewer.", outputs, cancellationToken);
    }

    /// <summary>Per-run event writer. Serializes appends so sequence numbers and state stay consistent under parallel nodes.</summary>
    private sealed class RunExecution(string runId, IEventStore eventStore, TimeProvider timeProvider, EngineOptions options, RunState state, WorkflowGraph graph) : IDisposable
    {
        /// <summary>Current graph; replaced (never mutated) when a node's plan is applied.</summary>
        public WorkflowGraph Graph { get; set; } = graph;

        private readonly SemaphoreSlim _lock = new(1, 1);
        private long _sequence = state.LastSequence;

        public string RunId => runId;

        public RunState State => state;

        public Task EmitAsync(
            RunEventType type, string? nodeId, string actor, string? message, CancellationToken cancellationToken, params (string Key, string Value)[] data) =>
            WriteAsync(null, type, nodeId, actor, message, data, cancellationToken);

        /// <summary>Writes a system event only if <paramref name="condition"/> holds, evaluated under the lock (check-and-write is atomic).</summary>
        public Task EmitIfAsync(
            Func<RunState, bool> condition, RunEventType type, string? nodeId, string? message, (string Key, string Value)[] data, CancellationToken cancellationToken) =>
            WriteAsync(condition, type, nodeId, SystemActor, message, data, cancellationToken);

        private async Task WriteAsync(
            Func<RunState, bool>? condition, RunEventType type, string? nodeId, string actor, string? message,
            (string Key, string Value)[] data, CancellationToken cancellationToken)
        {
            await _lock.WaitAsync(cancellationToken);
            try
            {
                if (condition is not null && !condition(state))
                {
                    return;
                }

                var runEvent = new RunEvent(++_sequence, runId, type, timeProvider.GetUtcNow(), actor)
                {
                    NodeId = nodeId,
                    Message = message,
                    Data = data.ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal),
                };
                await eventStore.AppendAsync(runEvent, cancellationToken);
                state.Apply(runEvent);
                options.OnEvent?.Invoke(runEvent);
            }
            finally
            {
                _lock.Release();
            }
        }

        public void Dispose() => _lock.Dispose();
    }
}

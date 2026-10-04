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

        using var run = new RunExecution(runId, eventStore, timeProvider, _options, new RunState());
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

        using var run = new RunExecution(runId, eventStore, timeProvider, _options, state);
        await run.EmitAsync(RunEventType.RunResumed, null, SystemActor, $"Resuming from event #{state.LastSequence}.", cancellationToken);
        return await ExecuteAsync(run, workflow, outputs, cancellationToken);
    }

    private async Task<RunState> ExecuteAsync(
        RunExecution run, WorkflowDefinition workflow, ConcurrentDictionary<string, Artifact> outputs, CancellationToken cancellationToken)
    {
        var graph = workflow.Graph;
        var startedAt = timeProvider.GetUtcNow();
        var running = new Dictionary<Task, string>();
        string? stopReason = null;

        while (true)
        {
            foreach (var node in graph.TopologicalOrder)
            {
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
                else if (status.Status is NodeStatus.Pending or NodeStatus.Ready or NodeStatus.Running
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

        foreach (var node in graph.TopologicalOrder.Where(n => run.State.Nodes[n.Id].Status == NodeStatus.Pending))
        {
            var blockedBy = graph.TransitiveDependencies(node.Id)
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
        var inputs = workflow.Graph.TransitiveDependencies(node.Id).ToDictionary(n => n.Id, n => outputs[n.Id], StringComparer.Ordinal);

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

        string? feedback = null;
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

                var (output, actor, rationale, failure, approvalReasons) = await AttemptAsync(run, workflow, node, executor, inputs, total, feedback, cancellationToken);
                if (failure is null && approvalReasons.Count > 0)
                {
                    await artifactStore.SaveAsync(run.RunId, output!, cancellationToken);
                    await run.EmitAsync(RunEventType.ApprovalRequested, node.Id, actor, string.Join(" ", approvalReasons), cancellationToken,
                        ("phase", "output"), ("outputHash", output!.Hash));
                    return;
                }

                if (failure is null)
                {
                    await CompleteAsync(run, node, inputs, output!, actor, rationale, outputs, cancellationToken);
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

    private static async Task<(Artifact? Output, string Actor, string? Rationale, string? Failure, IReadOnlyList<string> ApprovalReasons)> AttemptAsync(
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
            return (null, SystemActor, null, $"Executor threw {ex.GetType().Name}: {ex.Message}", []);
        }

        if (!result.Succeeded || result.Output is null)
        {
            return (null, result.Actor, null, result.Error ?? "Executor returned no output.", []);
        }

        var (gateFailure, approvalReasons) = await EvaluateGatesAsync(run, node, workflow.ExitGatesFor(node), "exit", new GateContext(run.RunId, node, inputs, result.Output), cancellationToken);
        return (result.Output, result.Actor, result.Rationale, gateFailure, approvalReasons);
    }

    private async Task CompleteAsync(
        RunExecution run, WorkflowNode node, IReadOnlyDictionary<string, Artifact> inputs, Artifact output, string actor, string? rationale,
        ConcurrentDictionary<string, Artifact> outputs, CancellationToken cancellationToken)
    {
        await artifactStore.SaveAsync(run.RunId, output, cancellationToken);
        outputs[node.Id] = output;
        await run.EmitAsync(RunEventType.NodeSucceeded, node.Id, actor, rationale, cancellationToken,
            ("outputHash", output.Hash),
            ("inputHashes", string.Join(';', inputs.Values.Select(a => $"{a.NodeId}={a.Hash}"))));

        if (await _checkpoints.CreateAsync(run.RunId, $"after {node.Id}", cancellationToken) is { } checkpoint)
        {
            await run.EmitAsync(RunEventType.CheckpointCreated, node.Id, SystemActor, null, cancellationToken, ("checkpointId", checkpoint));
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
        if (output.Hash != run.State.Nodes[node.Id].OutputHash)
        {
            throw new InvalidOperationException($"Approved output for '{node.Id}' changed after review (hash mismatch).");
        }

        var inputs = workflow.Graph.TransitiveDependencies(node.Id).ToDictionary(n => n.Id, n => outputs[n.Id], StringComparer.Ordinal);
        await CompleteAsync(run, node, inputs, output, "human-approved", "Output approved by reviewer.", outputs, cancellationToken);
    }

    /// <summary>Per-run event writer. Serializes appends so sequence numbers and state stay consistent under parallel nodes.</summary>
    private sealed class RunExecution(string runId, IEventStore eventStore, TimeProvider timeProvider, EngineOptions options, RunState state) : IDisposable
    {
        private readonly SemaphoreSlim _lock = new(1, 1);
        private long _sequence = state.LastSequence;

        public string RunId => runId;

        public RunState State => state;

        public async Task EmitAsync(
            RunEventType type, string? nodeId, string actor, string? message, CancellationToken cancellationToken, params (string Key, string Value)[] data)
        {
            await _lock.WaitAsync(cancellationToken);
            try
            {
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

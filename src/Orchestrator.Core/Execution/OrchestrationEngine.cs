using System.Collections.Concurrent;
using System.Globalization;
using Orchestrator.Core.Events;
using Orchestrator.Core.Graph;
using Orchestrator.Core.State;

namespace Orchestrator.Core.Execution;

/// <summary>
/// Executes a <see cref="WorkflowDefinition"/> as a dependency graph: every node whose dependencies
/// have succeeded starts immediately (bounded by <see cref="EngineOptions.MaxParallelism"/>), so
/// independent branches run in parallel and join nodes wait for all of their inputs. Each node
/// passes entry gates, executes, then passes exit gates. All state changes go through the event log.
/// </summary>
public sealed class OrchestrationEngine(
    IEventStore eventStore,
    IArtifactStore artifactStore,
    TimeProvider timeProvider,
    EngineOptions? options = null)
{
    private const string SystemActor = "system";
    private readonly EngineOptions _options = options ?? new EngineOptions();

    public async Task<RunState> RunAsync(string runId, string changeRequest, WorkflowDefinition workflow, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(workflow);

        using var run = new RunExecution(runId, eventStore, timeProvider, _options);
        var graph = workflow.Graph;
        await run.EmitAsync(RunEventType.RunStarted, null, SystemActor, changeRequest, cancellationToken,
            ("nodes", string.Join(',', graph.TopologicalOrder.Select(n => n.Id))));

        var outputs = new ConcurrentDictionary<string, Artifact>(StringComparer.Ordinal);
        var running = new Dictionary<Task, string>();

        while (true)
        {
            foreach (var node in graph.TopologicalOrder)
            {
                if (running.Count >= _options.MaxParallelism)
                {
                    break;
                }

                if (run.State.Nodes[node.Id].Status == NodeStatus.Pending
                    && node.DependsOn.All(d => run.State.Nodes[d].Status == NodeStatus.Succeeded))
                {
                    await run.EmitAsync(RunEventType.NodeReady, node.Id, SystemActor, null, cancellationToken);
                    running[RunNodeAsync(run, workflow, node, changeRequest, outputs, cancellationToken)] = node.Id;
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

        foreach (var node in graph.TopologicalOrder.Where(n => run.State.Nodes[n.Id].Status == NodeStatus.Pending))
        {
            var blockedBy = graph.TransitiveDependencies(node.Id)
                .Where(d => run.State.Nodes[d.Id].Status == NodeStatus.Failed)
                .Select(d => d.Id);
            await run.EmitAsync(RunEventType.NodeSkipped, node.Id, SystemActor, $"Upstream failed: {string.Join(", ", blockedBy)}", cancellationToken);
        }

        var succeeded = run.State.Nodes.Values.All(n => n.Status == NodeStatus.Succeeded);
        await run.EmitAsync(succeeded ? RunEventType.RunCompleted : RunEventType.RunFailed, null, SystemActor,
            succeeded ? "All nodes succeeded." : "One or more nodes failed.", cancellationToken);

        return run.State;
    }

    private async Task RunNodeAsync(
        RunExecution run,
        WorkflowDefinition workflow,
        WorkflowNode node,
        string changeRequest,
        ConcurrentDictionary<string, Artifact> outputs,
        CancellationToken cancellationToken)
    {
        var inputs = workflow.Graph.TransitiveDependencies(node.Id).ToDictionary(n => n.Id, n => outputs[n.Id], StringComparer.Ordinal);

        if (!await PassesGatesAsync(run, node, workflow.EntryGatesFor(node), "entry", new GateContext(run.RunId, node, inputs, null), cancellationToken))
        {
            return;
        }

        var attempt = run.State.Nodes[node.Id].Attempts + 1;
        await run.EmitAsync(RunEventType.NodeStarted, node.Id, SystemActor, null, cancellationToken,
            ("attempt", attempt.ToString(CultureInfo.InvariantCulture)));

        NodeResult result;
        try
        {
            result = await workflow.ExecutorFor(node).ExecuteAsync(
                new NodeExecutionContext(run.RunId, node, changeRequest, inputs, attempt), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await run.EmitAsync(RunEventType.NodeFailed, node.Id, SystemActor, $"Executor threw {ex.GetType().Name}: {ex.Message}", cancellationToken);
            return;
        }

        if (!result.Succeeded || result.Output is null)
        {
            await run.EmitAsync(RunEventType.NodeFailed, node.Id, result.Actor, result.Error ?? "Executor returned no output.", cancellationToken);
            return;
        }

        if (!await PassesGatesAsync(run, node, workflow.ExitGatesFor(node), "exit", new GateContext(run.RunId, node, inputs, result.Output), cancellationToken))
        {
            return;
        }

        await artifactStore.SaveAsync(run.RunId, result.Output, cancellationToken);
        outputs[node.Id] = result.Output;
        await run.EmitAsync(RunEventType.NodeSucceeded, node.Id, result.Actor, result.Rationale, cancellationToken,
            ("outputHash", result.Output.Hash),
            ("inputHashes", string.Join(';', inputs.Values.Select(a => $"{a.NodeId}={a.Hash}"))));
    }

    private static async Task<bool> PassesGatesAsync(
        RunExecution run, WorkflowNode node, IReadOnlyList<IGate> gates, string phase, GateContext context, CancellationToken cancellationToken)
    {
        foreach (var gate in gates)
        {
            var result = await gate.EvaluateAsync(context, cancellationToken);
            await run.EmitAsync(result.Passed ? RunEventType.GatePassed : RunEventType.GateFailed, node.Id, SystemActor, result.Reason, cancellationToken,
                ("gate", gate.Name), ("phase", phase));
            if (!result.Passed)
            {
                await run.EmitAsync(RunEventType.NodeFailed, node.Id, SystemActor, $"{phase} gate '{gate.Name}' failed: {result.Reason}", cancellationToken);
                return false;
            }
        }

        return true;
    }

    /// <summary>Per-run event writer. Serializes appends so sequence numbers and state stay consistent under parallel nodes.</summary>
    private sealed class RunExecution(string runId, IEventStore eventStore, TimeProvider timeProvider, EngineOptions options) : IDisposable
    {
        private readonly SemaphoreSlim _lock = new(1, 1);
        private long _sequence;

        public string RunId => runId;

        public RunState State { get; } = new();

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
                State.Apply(runEvent);
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

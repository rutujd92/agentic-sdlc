using System.Collections.Concurrent;
using System.Diagnostics;
using Orchestrator.Core.Execution;

namespace Orchestrator.Tests.Fakes;

/// <summary>
/// Configurable executor shared by all nodes of a test run. Records call order, timing and peak concurrency.
/// </summary>
public sealed class FakeExecutor : INodeExecutor
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private int _running;
    private int _peak;

    public TimeSpan Delay { get; init; } = TimeSpan.Zero;

    public HashSet<string> FailingNodes { get; init; } = [];

    public HashSet<string> ThrowingNodes { get; init; } = [];

    public ConcurrentQueue<string> Started { get; } = new();

    public ConcurrentDictionary<string, (TimeSpan Start, TimeSpan End)> Timings { get; } = new();

    public ConcurrentDictionary<string, NodeExecutionContext> Contexts { get; } = new();

    public int PeakConcurrency => _peak;

    public async Task<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var id = context.Node.Id;
        Started.Enqueue(id);
        Contexts[id] = context;
        var start = _clock.Elapsed;
        var now = Interlocked.Increment(ref _running);
        InterlockedMax(ref _peak, now);
        try
        {
            await Task.Delay(Delay, cancellationToken);
            if (ThrowingNodes.Contains(id))
            {
                throw new InvalidOperationException($"boom in {id}");
            }

            return FailingNodes.Contains(id)
                ? NodeResult.Failure($"{id} could not complete", "agent:fake")
                : NodeResult.Success(Artifact.Create(id, $"output of {id}"), "agent:fake", $"{id} done");
        }
        finally
        {
            Interlocked.Decrement(ref _running);
            Timings[id] = (start, _clock.Elapsed);
        }
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}

public sealed class FakeGate(string name, Func<GateContext, bool> passes, string reason = "fake gate") : IGate
{
    public string Name => name;

    public int Evaluations { get; private set; }

    public Task<GateResult> EvaluateAsync(GateContext context, CancellationToken cancellationToken)
    {
        Evaluations++;
        return Task.FromResult(passes(context) ? GateResult.Pass("ok") : GateResult.Fail(reason));
    }
}

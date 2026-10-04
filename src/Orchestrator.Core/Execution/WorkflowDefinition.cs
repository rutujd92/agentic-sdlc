using Orchestrator.Core.Graph;

namespace Orchestrator.Core.Execution;

/// <summary>Graph plus the executor and gates for each node.</summary>
public sealed record WorkflowDefinition(WorkflowGraph Graph, Func<WorkflowNode, INodeExecutor> ExecutorFor)
{
    public Func<WorkflowNode, IReadOnlyList<IGate>> EntryGatesFor { get; init; } = _ => [];

    public Func<WorkflowNode, IReadOnlyList<IGate>> ExitGatesFor { get; init; } = _ => [];

    public Func<WorkflowNode, RetryPolicy> RetryPolicyFor { get; init; } = _ => RetryPolicy.None;

    /// <summary>Executors tried in order (one attempt each) after the primary exhausts its retries.</summary>
    public Func<WorkflowNode, IReadOnlyList<INodeExecutor>> FallbacksFor { get; init; } = _ => [];
}

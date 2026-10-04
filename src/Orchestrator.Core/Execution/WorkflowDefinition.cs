using Orchestrator.Core.Graph;

namespace Orchestrator.Core.Execution;

/// <summary>Graph plus the executor and gates for each node.</summary>
public sealed record WorkflowDefinition(WorkflowGraph Graph, Func<WorkflowNode, INodeExecutor> ExecutorFor)
{
    public Func<WorkflowNode, IReadOnlyList<IGate>> EntryGatesFor { get; init; } = _ => [];

    public Func<WorkflowNode, IReadOnlyList<IGate>> ExitGatesFor { get; init; } = _ => [];
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orchestrator.Core.Graph;

/// <param name="Node">Existing node that gains a dependency.</param>
public sealed record DependencyEdge(string Node, string DependsOn);

/// <summary>A planner's proposal to extend the running graph, with the reason recorded in the audit log.</summary>
public sealed record GraphChange(IReadOnlyList<WorkflowNode> AddedNodes, IReadOnlyList<DependencyEdge> AddedDependencies, string Rationale)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public bool IsEmpty => AddedNodes.Count == 0 && AddedDependencies.Count == 0;

    /// <summary>Agents cannot add high-impact work without a human checkpoint: migrations, merges and high-risk nodes always require approval.</summary>
    public GraphChange Governed() => this with
    {
        AddedNodes = AddedNodes
            .Select(n => n.Kind is NodeKind.Migration or NodeKind.Merge || n.Risk == RiskLevel.High ? n with { RequiresApproval = true } : n)
            .ToArray(),
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static GraphChange FromJson(string json) =>
        JsonSerializer.Deserialize<GraphChange>(json, Json) ?? throw new InvalidOperationException("Empty graph change.");
}

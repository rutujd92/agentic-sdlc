using Orchestrator.Core.Graph;

switch (args.FirstOrDefault())
{
    case "graph":
        PrintGraph(StandardSdlcGraph.Create());
        return 0;
    default:
        Console.WriteLine("Usage: orchestrator <command>");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  graph    Show the standard SDLC dependency graph and its parallel layers");
        return args.Length == 0 ? 0 : 1;
}

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

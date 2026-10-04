using Orchestrator.Core.Execution;
using Orchestrator.Core.Governance;
using Orchestrator.Core.Graph;

namespace Orchestrator.Tests.Governance;

public class OpenQuestionsGateTests
{
    private static GateContext Context(string content) =>
        new("r", new WorkflowNode("requirements", NodeKind.Requirements), new Dictionary<string, Artifact>(), Artifact.Create("requirements", content));

    [Fact]
    public async Task Open_questions_require_human_clarification()
    {
        var result = await new OpenQuestionsGate().EvaluateAsync(Context("# R\n\n## Open questions\n- Which domains?\n- Block or warn?\n\n## Assumptions\n- x\n"), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.True(result.RequiresApproval);
        Assert.Contains("2 open question", result.Reason, StringComparison.Ordinal);
        Assert.Contains("Which domains?", result.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("# R\n\n## Open questions\n- None\n")]
    [InlineData("# R\n\n## Assumptions\n- x\n")]
    public async Task No_open_questions_passes(string content)
    {
        var result = await new OpenQuestionsGate().EvaluateAsync(Context(content), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.False(result.RequiresApproval);
    }
}

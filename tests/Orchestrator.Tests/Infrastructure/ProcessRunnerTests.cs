using Orchestrator.Infrastructure.Workspace;

namespace Orchestrator.Tests.Infrastructure;

public class ProcessRunnerTests
{
    // Regression: MSBuild worker nodes / build servers outlived `dotnet build`, inherited the redirected
    // output pipes and made the validate node wait forever. Child processes must run with them disabled.
    [Fact]
    public async Task Child_processes_run_with_msbuild_node_reuse_and_build_servers_disabled()
    {
        var result = await ProcessRunner.RunAsync("/usr/bin/env", Path.GetTempPath(), [], CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Contains("MSBUILDDISABLENODEREUSE=1", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("DOTNET_CLI_USE_MSBUILD_SERVER=0", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("UseSharedCompilation=false", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Arguments_are_passed_verbatim_without_a_shell()
    {
        var result = await ProcessRunner.RunAsync("/bin/echo", Path.GetTempPath(), ["a b", "$(whoami)", ";rm -rf /"], CancellationToken.None);

        Assert.Equal("a b $(whoami) ;rm -rf /", result.StandardOutput.Trim());
    }
}

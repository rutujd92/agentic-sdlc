using System.Diagnostics;

namespace Orchestrator.Infrastructure.Workspace;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>Runs a process with an argument list (never a shell string), so inputs cannot inject commands.</summary>
public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(string fileName, string workingDirectory, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        // MSBuild worker nodes, the MSBuild server and the Roslyn compiler server outlive the build and inherit the
        // redirected stdout/stderr pipes, so reading to end would wait for them forever. Run builds without them.
        info.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        info.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        info.Environment["UseSharedCompilation"] = "false";

        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start '{fileName}'.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new ProcessResult(process.ExitCode, await stdout, await stderr);
    }
}

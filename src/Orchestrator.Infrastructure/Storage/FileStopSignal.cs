using Orchestrator.Core.Execution;

namespace Orchestrator.Infrastructure.Storage;

/// <summary>Stop is requested by creating <c>&lt;root&gt;/&lt;runId&gt;/STOP</c> (the CLI <c>stop</c> command does this).</summary>
public sealed class FileStopSignal(string rootDirectory) : IStopSignal
{
    public bool IsStopRequested(string runId) => File.Exists(StopFile(rootDirectory, runId));

    public static string StopFile(string rootDirectory, string runId) => Path.Combine(RunPaths.Run(rootDirectory, runId), "STOP");
}

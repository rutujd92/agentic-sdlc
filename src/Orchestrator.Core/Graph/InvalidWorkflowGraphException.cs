namespace Orchestrator.Core.Graph;

public sealed class InvalidWorkflowGraphException : Exception
{
    public InvalidWorkflowGraphException()
        : this([])
    {
    }

    public InvalidWorkflowGraphException(string message)
        : this([message])
    {
    }

    public InvalidWorkflowGraphException(string message, Exception innerException)
        : base(message, innerException)
    {
        Errors = [message];
    }

    public InvalidWorkflowGraphException(IReadOnlyList<string> errors)
        : base("Invalid workflow graph: " + string.Join("; ", errors))
    {
        Errors = errors;
    }

    public IReadOnlyList<string> Errors { get; }
}

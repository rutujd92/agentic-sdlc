namespace Orchestrator.Core.Events;

public enum RunEventType
{
    RunStarted,
    NodeReady,
    NodeStarted,
    GatePassed,
    GateFailed,
    NodeSucceeded,
    NodeFailed,
    NodeSkipped,
    RetryScheduled,
    FallbackUsed,
    ApprovalRequested,
    ApprovalGranted,
    ApprovalRejected,
    PolicyViolation,
    CheckpointCreated,
    RolledBack,
    Replanned,
    NodeInvalidated,
    SafeStopped,
    RunResumed,
    RunCompleted,
    RunFailed,
}

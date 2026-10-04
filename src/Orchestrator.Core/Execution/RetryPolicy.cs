namespace Orchestrator.Core.Execution;

/// <summary>Bounded retry with exponential backoff. <see cref="MaxAttempts"/> includes the first attempt.</summary>
public sealed record RetryPolicy(int MaxAttempts, TimeSpan InitialBackoff, double BackoffMultiplier = 2)
{
    public static RetryPolicy None { get; } = new(1, TimeSpan.Zero);

    public TimeSpan DelayBeforeAttempt(int nextAttempt) =>
        TimeSpan.FromTicks((long)(InitialBackoff.Ticks * Math.Pow(BackoffMultiplier, nextAttempt - 2)));
}

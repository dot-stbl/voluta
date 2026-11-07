namespace Voluta.Abstractions.Runtime;

/// <summary>
///     Node retry policy: exponential backoff with a host-tunable retry predicate.
///     Attached per node via <c>NodeOptions.Retry</c> (builder); the attempt number is
///     visible as <c>GraphContext.Attempt</c> and persisted on checkpoint snapshots
///     (<c>TaskAttempts</c>) so a crash mid-retry resumes at the next attempt.
/// </summary>
public sealed class RetryPolicy
{
    private RetryPolicy(int maxAttempts, TimeSpan baseDelay, Func<Exception, bool>? retryOn)
    {
        MaxAttempts = maxAttempts;
        BaseDelay = baseDelay;
        RetryOn = retryOn;
    }

    /// <summary>
    ///     Total executions (1 = no retry). Always &gt;= 1; validated again at compile
    ///     (<c>graph.invalid_retry_policy</c>).
    /// </summary>
    public int MaxAttempts { get; }

    /// <summary>
    ///     Delay before retry k (1-based) = <see cref="BaseDelay" /> * 2^(k-1).
    /// </summary>
    public TimeSpan BaseDelay { get; }

    /// <summary>
    ///     Retry predicate; <see langword="null" /> means the static default
    ///     (retry every exception except <see cref="OperationCanceledException" />).
    /// </summary>
    public Func<Exception, bool>? RetryOn { get; }

    /// <summary>
    ///     Creates an exponential backoff policy.
    /// </summary>
    /// <param name="maxAttempts">Total executions (1 = no retry); must be &gt;= 1.</param>
    /// <param name="baseDelay">Delay before the first retry; must be positive and not near overflow.</param>
    /// <param name="retryOn">
    ///     Optional predicate; <see langword="null" /> uses the static default
    ///     (retry everything except <see cref="OperationCanceledException" />).
    /// </param>
    /// <returns>Immutable policy instance.</returns>
    /// <exception cref="System.ArgumentOutOfRangeException">
    ///     <paramref name="maxAttempts" /> &lt; 1 or <paramref name="baseDelay" /> non-positive / near overflow.
    /// </exception>
    public static RetryPolicy Exponential(
        int maxAttempts,
        TimeSpan baseDelay,
        Func<Exception, bool>? retryOn = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        return baseDelay <= TimeSpan.Zero
               || baseDelay.Ticks > TimeSpan.MaxValue.Ticks / 2
            ? throw new ArgumentOutOfRangeException(
                nameof(baseDelay),
                baseDelay,
                "Retry base delay must be positive and small enough for exponential growth.")
            : new RetryPolicy(maxAttempts, baseDelay, retryOn);
    }

    /// <summary>
    ///     Decides whether <paramref name="exception" /> triggers the next attempt:
    ///     <see cref="RetryOn" /> when set, otherwise the static default
    ///     (everything except <see cref="OperationCanceledException" />).
    /// </summary>
    /// <param name="exception">Exception thrown by the failed attempt.</param>
    /// <returns><see langword="true" /> when the node should retry.</returns>
    public bool ShouldRetry(Exception exception)
    {
        return RetryOn is { } retryOn
            ? retryOn(exception)
            : exception is not OperationCanceledException;
    }


    /// <summary>
    ///     Delay before retry <paramref name="completedAttempt" /> + 1:
    ///     <see cref="BaseDelay" /> * 2^(<paramref name="completedAttempt" /> - 1).
    ///     The growth exponent is capped so near-overflow base delays never throw.
    /// </summary>
    /// <param name="completedAttempt">1-based attempt number that just failed.</param>
    /// <returns>Backoff delay to wait before the next attempt.</returns>
    public TimeSpan DelayFor(int completedAttempt)
    {
        // 1L << 63 is negative (long sign bit) — cap the shift one bit below the design's 63.
        var shift = Math.Clamp(completedAttempt - 1, 0, 62);
        var multiplier = 1L << shift;
        var ticks = BaseDelay.Ticks <= TimeSpan.MaxValue.Ticks / multiplier
            ? BaseDelay.Ticks * multiplier
            : TimeSpan.MaxValue.Ticks;
        return TimeSpan.FromTicks(ticks);
    }
}

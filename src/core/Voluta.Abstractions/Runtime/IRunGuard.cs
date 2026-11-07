namespace Voluta.Abstractions.Runtime;

/// <summary>
///     Guard verdict for one superstep boundary evaluation.
/// </summary>
public enum RunGuardAction
{
    /// <summary>No objection — the run proceeds into the next superstep.</summary>
    Continue = 0,

    /// <summary>Park the run before the next superstep (Interrupted snapshot + StopReason).</summary>
    Stop = 1,
}

/// <summary>
///     Observed run state handed to <see cref="IRunGuard.Evaluate" /> before
///     superstep <see cref="NextStep" /> is scheduled.
/// </summary>
public sealed class RunGuardContext
{
    /// <summary>Superstep about to run (1-based, same scale as CompileOptions.RecursionLimit).</summary>
    public required long NextStep { get; init; }

    /// <summary>Committed supersteps so far.</summary>
    public required long CompletedSteps { get; init; }

    /// <summary>Run start time captured once at run entry (stream / continue start).</summary>
    public required DateTimeOffset StartedAt { get; init; }
}

/// <summary>
///     Guard decision: park with a reason, or continue.
/// </summary>
/// <param name="Action">Continue or Stop.</param>
/// <param name="Reason">Machine-readable stop reason when <paramref name="Action" /> is Stop.</param>
public readonly record struct RunGuardDecision(RunGuardAction Action, string? Reason)
{
    /// <summary>Proceed into the next superstep.</summary>
    public static RunGuardDecision Continue { get; } = new(RunGuardAction.Continue, null);

    /// <summary>Park the run before the next superstep with the given reason.</summary>
    /// <param name="reason">Machine-readable reason (maps to a typed StopReason on the snapshot).</param>
    /// <returns>Stop decision carrying <paramref name="reason" />.</returns>
    public static RunGuardDecision Stop(string reason)
    {
        return new RunGuardDecision(RunGuardAction.Stop, reason);
    }
}

/// <summary>
///     Policy hook evaluated before each superstep (before the recursion-limit and
///     static-pause checks). First Stop wins; a stop produces an <c>Interrupted</c>
///     snapshot with a typed StopReason — never a Failed run.
/// </summary>
public interface IRunGuard
{
    /// <summary>
    ///     Evaluates the run state before superstep <paramref name="context.NextStep" />.
    /// </summary>
    /// <param name="context">Observed run state.</param>
    /// <returns>Continue or Stop (+ reason).</returns>
    public RunGuardDecision Evaluate(RunGuardContext context);
}

/// <summary>
///     Built-in guard factories: deadline and budget stops.
/// </summary>
/// <remarks>
///     <c>Deadline(TimeSpan)</c> is anchored at construction time (documented): a guard
///     created before a long compile still measures from its construction. Hosts wanting a
///     run-relative deadline pass an absolute <see cref="DateTimeOffset" /> or capture
///     <see cref="RunGuardContext.StartedAt" /> via <see cref="Budget" />.
///     Deadline guards read the current time through a <see cref="TimeProvider" />: the
///     provider-less factories default to <see cref="TimeProvider.System" /> (wall-clock
///     behavior unchanged), while the overloads taking an explicit provider let hosts
///     freeze or advance time deterministically in tests (<c>FakeTimeProvider</c>).
/// </remarks>
public static class RunGuards
{
    /// <summary>Reason string used by deadline guards (maps to StopReason.DeadlineExceeded).</summary>
    public const string DeadlineExceeded = "deadline_exceeded";

    /// <summary>Reason string used by budget guards (maps to StopReason.BudgetExhausted).</summary>
    public const string BudgetExhausted = "budget_exhausted";

    /// <summary>
    ///     Stops the run once <paramref name="at" /> has passed.
    /// </summary>
    /// <param name="at">Absolute deadline.</param>
    /// <returns>Guard parking the run with reason <c>deadline_exceeded</c>.</returns>
    public static IRunGuard Deadline(DateTimeOffset at)
    {
        return new DeadlineGuard(at, TimeProvider.System);
    }

    /// <summary>
    ///     Stops the run once <paramref name="fromNow" /> (anchored at this call) has elapsed.
    /// </summary>
    /// <param name="fromNow">Relative duration from guard construction.</param>
    /// <returns>Guard parking the run with reason <c>deadline_exceeded</c>.</returns>
    public static IRunGuard Deadline(TimeSpan fromNow)
    {
        return new DeadlineGuard(TimeProvider.System.GetUtcNow().Add(fromNow), TimeProvider.System);
    }

    /// <summary>
    ///     Stops the run once <paramref name="at" /> has passed, reading "now" through
    ///     <paramref name="timeProvider" /> (injectable clock for deterministic tests).
    /// </summary>
    /// <param name="at">Absolute deadline.</param>
    /// <param name="timeProvider">Time source the guard reads on every evaluation.</param>
    /// <returns>Guard parking the run with reason <c>deadline_exceeded</c>.</returns>
    /// <exception cref="ArgumentException"><paramref name="timeProvider" /> is <see langword="null" />.</exception>
    public static IRunGuard Deadline(DateTimeOffset at, TimeProvider timeProvider)
    {
        return timeProvider is null
            ? throw new ArgumentException("Time provider must be non-null.", nameof(timeProvider))
            : new DeadlineGuard(at, timeProvider);
    }

    /// <summary>
    ///     Stops the run once <paramref name="fromNow" /> — anchored at this call against
    ///     <paramref name="timeProvider" /> — has elapsed (injectable clock for deterministic tests).
    /// </summary>
    /// <param name="fromNow">Relative duration from guard construction.</param>
    /// <param name="timeProvider">Time source the guard reads on every evaluation.</param>
    /// <returns>Guard parking the run with reason <c>deadline_exceeded</c>.</returns>
    /// <exception cref="ArgumentException"><paramref name="timeProvider" /> is <see langword="null" />.</exception>
    public static IRunGuard Deadline(TimeSpan fromNow, TimeProvider timeProvider)
    {
        return timeProvider is null
            ? throw new ArgumentException("Time provider must be non-null.", nameof(timeProvider))
            : new DeadlineGuard(timeProvider.GetUtcNow().Add(fromNow), timeProvider);
    }

    /// <summary>
    ///     Stops the run when <paramref name="isExhausted" /> returns
    ///     <see langword="true" /> for the observed context.
    /// </summary>
    /// <param name="isExhausted">Host budget predicate.</param>
    /// <returns>Guard parking the run with reason <c>budget_exhausted</c>.</returns>
    public static IRunGuard Budget(Func<RunGuardContext, bool> isExhausted)
    {
        return new BudgetGuard(isExhausted);
    }

    private sealed class DeadlineGuard(DateTimeOffset at, TimeProvider timeProvider) : IRunGuard
    {
        public RunGuardDecision Evaluate(RunGuardContext context)
        {
            return timeProvider.GetUtcNow() >= at
                ? RunGuardDecision.Stop(DeadlineExceeded)
                : RunGuardDecision.Continue;
        }
    }

    private sealed class BudgetGuard(Func<RunGuardContext, bool> isExhausted) : IRunGuard
    {
        public RunGuardDecision Evaluate(RunGuardContext context)
        {
            return isExhausted(context)
                ? RunGuardDecision.Stop(BudgetExhausted)
                : RunGuardDecision.Continue;
        }
    }
}

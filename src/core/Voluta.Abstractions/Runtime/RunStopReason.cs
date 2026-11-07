namespace Voluta.Abstractions.Runtime;

/// <summary>
///     Typed reason recorded on an <c>Interrupted</c> snapshot that was written by a
///     non-HITL pause: a guard stop (budget/deadline) or a static pause point
///     (InterruptBefore / InterruptAfter). <see cref="None" /> means the snapshot was
///     written for a regular HITL interrupt (or is not interrupted at all).
/// </summary>
/// <remarks>
///     Hosts distinguish pause kinds data-driven by the pair
///     (<see cref="Checkpoint.CheckpointSnapshot.PendingInterrupts" />,
///     <see cref="Checkpoint.CheckpointSnapshot.StopReason" />):
///     HITL = interrupts present + <see cref="None" />; static = empty + <c>Static*</c>;
///     guard = empty + <c>Budget/Deadline</c>. The run status stays <c>Interrupted</c> —
///     no new status enum values.
/// </remarks>
public enum RunStopReason
{
    /// <summary>Not a policy/static stop (regular HITL interrupt or not interrupted).</summary>
    None = 0,

    /// <summary>A budget guard predicate returned exhausted.</summary>
    BudgetExhausted = 1,

    /// <summary>A deadline guard passed its threshold.</summary>
    DeadlineExceeded = 2,

    /// <summary>Static pause before entering a node (CompileOptions.InterruptBefore).</summary>
    StaticBefore = 3,

    /// <summary>Static pause after a node's output was applied and routed (InterruptAfter).</summary>
    StaticAfter = 4,
}

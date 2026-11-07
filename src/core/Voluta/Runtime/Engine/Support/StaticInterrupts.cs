using Voluta.Abstractions.Checkpoint;
using Voluta.Abstractions.Runtime;
using Voluta.Graph;
using Voluta.Runtime.Engine.Tasks;

namespace Voluta.Runtime.Engine.Support;

/// <summary>
///     Static (non-HITL) pause points (v050): <c>CompileOptions.InterruptBefore</c> /
///     <c>InterruptAfter</c>. Evaluated on every superstep entry (before → park before the
///     step runs; after → park once the barrier applied writes and routing produced the
///     next ready set). Cycles re-park on every pass (LangGraph behavior). The parked
///     snapshot is <c>Interrupted</c> with an empty <see cref="CheckpointSnapshot.PendingInterrupts" />
///     and a typed <see cref="CheckpointSnapshot.StopReason" /> — hosts distinguish static
///     pauses from HITL interrupts and guard stops by the pair (PendingInterrupts, StopReason).
/// </summary>
internal static class StaticInterrupts
{
    /// <summary>
    ///     Whether any ready task is parked BEFORE entry at this superstep boundary.
    /// </summary>
    public static bool ShouldPauseBefore(
        GraphTopology topology,
        IReadOnlyList<ReadyTask> readyTasks)
    {
        if (topology.InterruptBefore.Count == 0)
        {
            return false;
        }

        foreach (var task in readyTasks)
        {
            if (topology.InterruptBefore.Contains(task.NodeName))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Whether any node executed in this superstep parks AFTER its output was applied.
    /// </summary>
    public static bool ShouldPauseAfter(
        GraphTopology topology,
        IReadOnlyList<string> executedNodes)
    {
        if (topology.InterruptAfter.Count == 0)
        {
            return false;
        }

        foreach (var name in executedNodes)
        {
            if (topology.InterruptAfter.Contains(name))
            {
                return true;
            }
        }

        return false;
    }
}

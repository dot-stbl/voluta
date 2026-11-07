using Voluta.Abstractions.Checkpoint;
using Voluta.Abstractions.Runtime;
using Voluta.Runtime.Engine.Tasks;

namespace Voluta.Runtime.Engine.Support;

/// <summary>
///     Step-local durable progress buffer for per-task writes (item 2) and retry attempts
///     (item 1): the engine puts a guarded progress snapshot after every completed task
///     (and when a retry schedules its next attempt) so a mid-superstep crash re-runs only
///     unfinished tasks. All puts are issued through one gate — sequentially, with batch
///     merging of completions that drain between awaits — so same-step guarded rewrites
///     never race (puts are never issued from concurrent task bodies racing each other).
///     Visible channel state is untouched: the store still applies only at the barrier.
/// </summary>
internal sealed class SuperstepProgress(
    string threadId,
    long step,
    string? lastNode,
    IReadOnlyList<string> stepNodeNames,
    IReadOnlyList<PendingSend> stepSends,
    RunCheckpointWriter checkpointer,
    ChannelStore store,
    IReadOnlyDictionary<string, IReadOnlyList<string>> joinArrivals)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object mutex = new();
    private readonly Dictionary<string, List<TaskChannelWrite>> writesByTask = new(StringComparer.Ordinal);
    private readonly List<string> completedTaskIds = [];
    private readonly Dictionary<string, int> attempts = new(StringComparer.Ordinal);
    private volatile bool suppressed;

    /// <summary>
    ///     True once the step faulted (or was cancelled) — further progress puts are
    ///     skipped; the failure/cancel path owns the thread state from here.
    /// </summary>
    public bool Suppressed => suppressed;

    /// <summary>
    ///     Buffers a completed task's writes and marks it complete (no put — the drain
    ///     batches completions and flushes once).
    /// </summary>
    public void Complete(string taskId, IReadOnlyList<TaskChannelWrite> writes)
    {
        lock (mutex)
        {
            writesByTask[taskId] = [.. writes];
            completedTaskIds.Add(taskId);
            attempts.Remove(taskId);
        }
    }

    /// <summary>Stops progress puts (step-level fault/cancel observed).</summary>
    public void Suppress()
    {
        suppressed = true;
    }

    /// <summary>
    ///     Issues one guarded progress put for everything buffered so far (drain-side;
    ///     batch merge point).
    /// </summary>
    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        if (suppressed)
        {
            return;
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            await PutAsync(cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    ///     Retry-side put (task body, between attempts): records the next attempt for the
    ///     task, then a guarded put — before the backoff delay, so a crash during the
    ///     delay resumes at the next attempt.
    /// </summary>
    public async Task RecordRetryAsync(string taskId, int nextAttempt, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            lock (mutex)
            {
                attempts[taskId] = nextAttempt;
            }

            await PutAsync(cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    ///     Current buffer contents for snapshot building after the drain concluded
    ///     (single-threaded at that point — the interrupt path reads without the gate).
    /// </summary>
    public (IReadOnlyList<PendingWrite> Writes, IReadOnlyList<string> Completed, IReadOnlyDictionary<string, int> Attempts) Current()
    {
        lock (mutex)
        {
            var writes = new List<PendingWrite>();
            foreach (var (taskId, taskWrites) in writesByTask)
            {
                foreach (var write in taskWrites)
                {
                    writes.Add(
                        new PendingWrite
                        {
                            TaskId = taskId,
                            ChannelName = write.Write.ChannelName,
                            Value = write.Write.Value,
                        });
                }
            }

            return (writes, [.. completedTaskIds], new Dictionary<string, int>(attempts, StringComparer.Ordinal));
        }
    }

    private async Task PutAsync(CancellationToken cancellationToken)
    {
        if (suppressed)
        {
            return;
        }

        var (writes, completed, attemptMap) = Current();
        var completedSet = new HashSet<string>(completed, StringComparer.Ordinal);
        // Unfinished send tasks of the current step stay in PendingSends so crash-resume
        // reconstructs the exact ready set (pull from NextNodes + these sends).
        List<PendingSend>? remainingSends = null;
        foreach (var send in stepSends)
        {
            if (completedSet.Contains(send.TaskId))
            {
                continue;
            }

            remainingSends ??= [];
            remainingSends.Add(send);
        }

        await checkpointer.PutAsync(
            RunEngineSnapshots.Build(
                threadId,
                step,
                GraphRunStatus.Running,
                store,
                lastNode,
                stepNodeNames,
                remainingSends ?? [],
                null,
                pendingWrites: writes,
                completedTaskIds: completed,
                taskAttempts: attemptMap,
                joinArrivals: joinArrivals,
                identity: checkpointer.Identity),
            cancellationToken);
    }
}

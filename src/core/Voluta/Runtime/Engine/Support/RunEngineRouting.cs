using Voluta.Abstractions.Channels;
using Voluta.Abstractions.Checkpoint;
using Voluta.Graph;
using Voluta.Runtime.Engine.Tasks;

namespace Voluta.Runtime.Engine.Support;

/// <summary>
///     Ready-set routing helpers.
/// </summary>
internal static class RunEngineRouting
{
    public static IReadOnlyList<string> ResolveNextNodes(
        GraphTopology topology,
        string source,
        IReadOnlyDictionary<string, object?> channelValues,
        object? resumePayload,
        bool isResume = false,
        string? resumeKind = null)
    {
        if (topology.ConditionalEdges.TryGetValue(source, out var router))
        {
            var context = new GraphContext(
                source,
                channelValues,
                resumePayload,
                services: topology.Services,
                isResume: isResume,
                resumeKind: resumeKind);
            return [.. router(context)];
        }

        // Topology lists are immutable after compile; return as-is (callers must not mutate).
        return topology.StaticEdges.TryGetValue(source, out var targets)
            ? targets
            : [];
    }

    public static IReadOnlyList<ReadyTask> ToPullTasks(GraphTopology topology, IReadOnlyList<string> candidates)
    {
        if (candidates.Count == 0)
        {
            return [];
        }

        var filtered = new List<string>(candidates.Count);
        foreach (var name in candidates)
        {
            if (name == GraphConstants.End || !topology.Nodes.ContainsKey(name))
            {
                continue;
            }

            if (!filtered.Contains(name))
            {
                filtered.Add(name);
            }
        }

        if (filtered.Count == 0)
        {
            return [];
        }

        filtered.Sort(StringComparer.Ordinal);
        var tasks = new ReadyTask[filtered.Count];
        for (var index = 0; index < filtered.Count; index++)
        {
            var name = filtered[index];
            tasks[index] = new ReadyTask(name, name, null);
        }

        return tasks;
    }

    /// <summary>
    ///     Per-task progress resume (v050): drops tasks already completed in the crashed
    ///     step (<c>CompletedTaskIds</c>) and restores persisted retry attempts
    ///     (<c>TaskAttempts</c>) so the first execution of a resumed task continues at the
    ///     next attempt. Snapshot without progress → ready set returned unchanged.
    /// </summary>
    public static IReadOnlyList<ReadyTask> ExcludeCompleted(
        IReadOnlyList<ReadyTask> tasks,
        CheckpointSnapshot checkpoint)
    {
        if (checkpoint.CompletedTaskIds.Count == 0 && checkpoint.TaskAttempts.Count == 0)
        {
            return tasks;
        }

        var completed = new HashSet<string>(checkpoint.CompletedTaskIds, StringComparer.Ordinal);
        var result = new List<ReadyTask>(tasks.Count);
        foreach (var task in tasks)
        {
            if (completed.Contains(task.TaskId))
            {
                continue;
            }

            result.Add(
                checkpoint.TaskAttempts.TryGetValue(task.TaskId, out var attempt) && attempt > 0
                    ? new ReadyTask(task.NodeName, task.TaskId, task.TaskPayload)
                    {
                        StartAttempt = attempt,
                        // recorded value IS the next attempt to execute (n+1 after n failed)
                    }
                    : task);
        }

        return result;
    }

    /// <summary>
    ///     Join rehydration on resume (v050): the crashed step's completed tasks
    ///     (<paramref name="completedTaskIds" />) re-arrive at their joins before the resume
    ///     superstep — a boundary-put crash must not lose a firing whose sources all
    ///     completed. Restored epoch arrivals (<paramref name="arrivals" />, mutated) merge
    ///     with the re-derived ones; satisfied joins fire (epoch resets, target becomes ready).
    /// </summary>
    public static IReadOnlyList<ReadyTask> RehydrateJoins(
        GraphTopology topology,
        Dictionary<string, IReadOnlyList<string>> arrivals,
        IReadOnlyList<ReadyTask> ready,
        IReadOnlyList<string> completedTaskIds)
    {
        if (topology.JoinSources.Count == 0 || completedTaskIds.Count == 0)
        {
            return ready;
        }

        List<ReadyTask>? result = null;
        foreach (var (target, sources) in topology.JoinSources)
        {
            var arrived = arrivals.TryGetValue(target, out var list)
                ? new List<string>(list)
                : [];
            var rehydrated = false;
            foreach (var source in sources)
            {
                if (completedTaskIds.Contains(source) && !arrived.Contains(source))
                {
                    arrived.Add(source);
                    rehydrated = true;
                }
            }

            if (!rehydrated)
            {
                continue;
            }

            if (sources.All(arrived.Contains))
            {
                // Firing was (or would have been) due: epoch resets, target becomes ready.
                arrivals.Remove(target);
                result ??= [.. ready];
                if (!result.Any(task => string.Equals(task.NodeName, target, StringComparison.Ordinal)))
                {
                    result.Add(new ReadyTask(target, target, null));
                }
            }
            else
            {
                arrivals[target] = arrived;
            }
        }

        if (result is null)
        {
            return ready;
        }

        result.Sort(static (left, right) =>
        {
            var nodeCompare = string.CompareOrdinal(left.NodeName, right.NodeName);
            return nodeCompare != 0
                ? nodeCompare
                : string.CompareOrdinal(left.TaskId, right.TaskId);
        });
        return result;
    }
}

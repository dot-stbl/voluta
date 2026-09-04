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
}

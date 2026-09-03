using Voluta.Abstractions.Channels;
using Voluta.Abstractions.State;
using Voluta.Channels;
using Voluta.Channels.Custom;
using Voluta.Exceptions;
using Voluta.Exceptions.Run;
using Voluta.Runtime.Engine.Restore;

namespace Voluta.Runtime.Engine;

/// <summary>
///     Mutable channel map + versions for one run thread.
/// </summary>
internal sealed class ChannelStore
{
    private readonly Dictionary<string, IChannel> channels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GraphChannelDeclaration> declarations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> versions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, long>> versionsSeen = new(StringComparer.Ordinal);

    /// <summary>
    ///     Creates channels for the declared topology.
    /// </summary>
    public ChannelStore(IReadOnlyDictionary<string, GraphChannelDeclaration> channelDeclarations)
    {
        foreach (var (name, declaration) in channelDeclarations)
        {
            declarations[name] = declaration;
            channels[name] = ChannelFactory.Create(declaration);
            versions[name] = 0;
        }
    }

    /// <summary>
    ///     Current channel versions.
    /// </summary>
    public IReadOnlyDictionary<string, long> Versions => versions;

    /// <summary>
    ///     Deep-clones the per-node versions_seen map for checkpoint isolation.
    ///     Call only at snapshot build — not on every property read.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, long>> CloneVersionsSeen()
    {
        var clone = new Dictionary<string, IReadOnlyDictionary<string, long>>(
            versionsSeen.Count,
            StringComparer.Ordinal);
        foreach (var (nodeName, map) in versionsSeen)
        {
            clone[nodeName] = new Dictionary<string, long>(map, StringComparer.Ordinal);
        }

        return clone;
    }

    /// <summary>
    ///     Snapshots current channel values (one dictionary; channel Get clones as needed).
    /// </summary>
    public IReadOnlyDictionary<string, object?> SnapshotValues()
    {
        var snapshot = new Dictionary<string, object?>(channels.Count, StringComparer.Ordinal);
        foreach (var (name, channel) in channels)
        {
            snapshot[name] = channel.Get();
        }

        return snapshot;
    }

    /// <summary>
    ///     Restores values, versions, and versions_seen from a checkpoint.
    /// </summary>
    public void Restore(
        IReadOnlyDictionary<string, object?> values,
        IReadOnlyDictionary<string, long> channelVersions,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, long>> seen)
    {
        foreach (var (name, channel) in channels)
        {
            var declaration = declarations[name];
            var raw = values.TryGetValue(name, out var value) ? value : null;
            channel.Restore(
                ChannelRestoreCoercion.Coerce(name, raw, declaration.ValueType, declaration.Kind));
        }

        versions.Clear();
        foreach (var (name, version) in channelVersions)
        {
            versions[name] = version;
        }

        foreach (var name in channels.Keys)
        {
            _ = versions.TryAdd(name, 0);
        }

        versionsSeen.Clear();
        foreach (var (nodeName, map) in seen)
        {
            versionsSeen[nodeName] = new Dictionary<string, long>(map, StringComparer.Ordinal);
        }
    }

    /// <summary>
    ///     Seeds initial input writes before the first superstep (as step 0 apply).
    /// </summary>
    public void ApplyInputWrites(IEnumerable<ChannelWrite> writes)
    {
        ApplyGrouped(ChannelWriteGrouping.Group(writes));
    }

    /// <summary>
    ///     Applies node writes for a superstep in deterministic channel/task order.
    ///     Sorts once by channel name then task id, groups in a single pass, then applies
    ///     channels in key order (LastValue concurrent reject + Append merge order).
    /// </summary>
    public void ApplyWrites(IReadOnlyList<TaskChannelWrite> writes)
    {
        if (writes.Count == 0)
        {
            return;
        }

        var grouped = new Dictionary<string, List<object?>>(StringComparer.Ordinal);
        if (writes.Count == 1)
        {
            var single = writes[0];
            grouped[single.Write.ChannelName] = [single.Write.Value];
            ApplyGrouped(grouped);
            return;
        }

        // One sort: channel name, then task id (preserves Append order / concurrent detect).
        var ordered = new TaskChannelWrite[writes.Count];
        for (var index = 0; index < writes.Count; index++)
        {
            ordered[index] = writes[index];
        }

        Array.Sort(ordered, static (left, right) =>
        {
            var channelCompare = string.CompareOrdinal(left.Write.ChannelName, right.Write.ChannelName);
            return channelCompare != 0
                ? channelCompare
                : string.CompareOrdinal(left.TaskId, right.TaskId);
        });

        string? currentChannel = null;
        List<object?>? currentList = null;
        foreach (var item in ordered)
        {
            var channelName = item.Write.ChannelName;
            if (currentList is null || !string.Equals(currentChannel, channelName, StringComparison.Ordinal))
            {
                currentChannel = channelName;
                currentList = [];
                grouped[channelName] = currentList;
            }

            currentList.Add(item.Write.Value);
        }

        ApplyGrouped(grouped);
    }

    /// <summary>
    ///     Marks channels as seen by a node after it ran.
    /// </summary>
    public void MarkSeen(string nodeName)
    {
        if (!versionsSeen.TryGetValue(nodeName, out var map))
        {
            map = new Dictionary<string, long>(StringComparer.Ordinal);
            versionsSeen[nodeName] = map;
        }

        foreach (var (channelName, version) in versions)
        {
            map[channelName] = version;
        }
    }

    private void ApplyGrouped(IReadOnlyDictionary<string, List<object?>> grouped)
    {
        if (grouped.Count == 0)
        {
            return;
        }

        // Channel apply order: sorted keys (ApplyWrites groups in that order for multi-write;
        // input seeding may not — sort once here).
        var channelNames = new string[grouped.Count];
        var index = 0;
        foreach (var channelName in grouped.Keys)
        {
            channelNames[index++] = channelName;
        }

        Array.Sort(channelNames, StringComparer.Ordinal);

        foreach (var channelName in channelNames)
        {
            var values = grouped[channelName];
            if (!channels.TryGetValue(channelName, out var channel))
            {
                throw new GraphRunFailedException(
                    $"Write targeted undeclared channel '{channelName}'.");
            }

            try
            {
                channel.Update(values);
            }
            catch (GraphConcurrentUpdateException exception)
            {
                throw new GraphConcurrentUpdateException(
                    $"Channel '{channelName}': {exception.Message}");
            }
            catch (GraphException exception)
            {
                throw new GraphException(
                    exception.Code,
                    $"Channel '{channelName}': {exception.Message}",
                    exception);
            }

            versions[channelName] = versions.TryGetValue(channelName, out var version)
                ? version + 1
                : 1;
        }
    }
}

/// <summary>
///     Groups channel writes by name in deterministic order.
/// </summary>
file static class ChannelWriteGrouping
{
    public static Dictionary<string, List<object?>> Group(IEnumerable<ChannelWrite> writes)
    {
        var grouped = new Dictionary<string, List<object?>>(StringComparer.Ordinal);
        foreach (var write in writes.OrderBy(static item => item.ChannelName, StringComparer.Ordinal))
        {
            if (!grouped.TryGetValue(write.ChannelName, out var list))
            {
                list = [];
                grouped[write.ChannelName] = list;
            }

            list.Add(write.Value);
        }

        return grouped;
    }
}

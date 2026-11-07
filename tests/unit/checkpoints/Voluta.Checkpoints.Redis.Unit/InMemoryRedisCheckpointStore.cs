namespace Voluta.Checkpoints.Redis;

/// <summary>
///     In-memory <see cref="IRedisCheckpointStore" /> double for unit tests. Mirrors the real
///     store's Lua semantics under a single lock (each method body is the atomic primitive).
/// </summary>
internal sealed class InMemoryRedisCheckpointStore : IRedisCheckpointStore
{
    private readonly object gate = new();
    private readonly Dictionary<string, SortedDictionary<long, string>> sets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RedisCheckpointHead> heads = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<RedisCheckpointHead?> HeadAsync(string headKey, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult(heads.TryGetValue(headKey, out var head) ? head : null);
        }
    }

    /// <summary>Simulates the blind Lua script: unconditional member upsert + marker refresh.</summary>
    public Task BlindPutAsync(
        string threadKey,
        string headKey,
        long step,
        string json,
        string newToken,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            EnsureSet(threadKey)[step] = json;
            if (heads.TryGetValue(headKey, out var head) && head.Step < step)
            {
                heads[headKey] = new RedisCheckpointHead(step, newToken);
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>Simulates the guarded Lua script: marker CAS + member write in one primitive.</summary>
    public Task<bool> GuardedPutAsync(
        string threadKey,
        string headKey,
        long step,
        long newHeadStep,
        string json,
        long expectedStep,
        string? expectedToken,
        string newToken,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (expectedToken is { } token)
            {
                if (!heads.TryGetValue(headKey, out var head)
                    || head.Step != expectedStep
                    || head.Token != token)
                {
                    return Task.FromResult(false);
                }
            }
            else
            {
                // Legacy capture: the marker must still be absent and the latest score match.
                if (heads.ContainsKey(headKey)
                    || !sets.TryGetValue(threadKey, out var current)
                    || current.Count == 0
                    || current.Last().Key != expectedStep)
                {
                    return Task.FromResult(false);
                }
            }

            EnsureSet(threadKey)[step] = json;
            heads[headKey] = new RedisCheckpointHead(newHeadStep, newToken);
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<RedisCheckpointEntry>> ListAsync(
        string threadKey,
        CancellationToken cancellationToken)
    {
        if (!sets.TryGetValue(threadKey, out var set))
        {
            return Task.FromResult<IReadOnlyList<RedisCheckpointEntry>>([]);
        }

        var entries = set
            .Select(static pair => new RedisCheckpointEntry(pair.Key, pair.Value))
            .ToArray();
        return Task.FromResult<IReadOnlyList<RedisCheckpointEntry>>(entries);
    }

    public Task<RedisCheckpointEntry?> LatestAsync(
        string threadKey,
        CancellationToken cancellationToken)
    {
        if (!sets.TryGetValue(threadKey, out var set) || set.Count == 0)
        {
            return Task.FromResult<RedisCheckpointEntry?>(null);
        }

        var last = set.Last();
        return Task.FromResult<RedisCheckpointEntry?>(new RedisCheckpointEntry(last.Key, last.Value));
    }

    public Task<IReadOnlyList<string>> ListKeysAsync(string pattern, CancellationToken cancellationToken)
    {
        var prefix = pattern.EndsWith("*", StringComparison.Ordinal)
            ? pattern[..^1]
            : pattern;
        var keys = sets.Keys
            .Where(key => key.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(static key => key, StringComparer.Ordinal)
            .ToArray();
        return Task.FromResult<IReadOnlyList<string>>(keys);
    }

    private SortedDictionary<long, string> EnsureSet(string threadKey)
    {
        if (!sets.TryGetValue(threadKey, out var target))
        {
            target = new SortedDictionary<long, string>();
            sets[threadKey] = target;
        }

        return target;
    }
}

using StackExchange.Redis;

namespace Voluta.Checkpoints.Redis;

/// <summary>
///     <see cref="IRedisCheckpointStore" /> over a live Redis connection.
/// </summary>
internal sealed class RedisCheckpointStore(IConnectionMultiplexer multiplexer, int database) : IRedisCheckpointStore
{
    /// <summary>
    ///     Guarded put CAS (one atomic script, so no interleaving writer can slip between check
    ///     and write). ARGV: snapshot step, new head step, json, expected head step, expected
    ///     head token ('' = legacy capture without a marker), new head token. Verifies the head
    ///     marker still matches the writer's captured view (legacy: marker absent + sorted-set
    ///     latest equals the expected step), then writes the member and the marker together.
    ///     Returns 1 on write, 0 on CAS loss.
    /// </summary>
    private const string GuardedPutLua =
        """
        local hstep = redis.call('HGET', KEYS[2], 'step')
        local htoken = redis.call('HGET', KEYS[2], 'token')
        if ARGV[5] ~= '' then
            if hstep == false or htoken == false or hstep ~= ARGV[4] or htoken ~= ARGV[5] then
                return 0
            end
        else
            if hstep ~= false then
                return 0
            end
            local latest = redis.call('ZRANGE', KEYS[1], -1, -1, 'WITHSCORES')
            if latest[1] == false or tonumber(latest[2]) ~= tonumber(ARGV[4]) then
                return 0
            end
        end
        redis.call('HSET', KEYS[2], 'step', ARGV[2], 'token', ARGV[6])
        redis.call('ZREMRANGEBYSCORE', KEYS[1], ARGV[1], ARGV[1])
        redis.call('ZADD', KEYS[1], ARGV[1], ARGV[3])
        return 1
        """;

    /// <summary>
    ///     Blind put (one atomic script): unconditional member upsert; when a head marker
    ///     exists and the step advances it, the marker is refreshed with the new token.
    ///     ARGV: step, json, new token.
    /// </summary>
    private const string BlindPutLua =
        """
        redis.call('ZREMRANGEBYSCORE', KEYS[1], ARGV[1], ARGV[1])
        redis.call('ZADD', KEYS[1], ARGV[1], ARGV[2])
        local hstep = redis.call('HGET', KEYS[2], 'step')
        if hstep ~= false and tonumber(hstep) < tonumber(ARGV[1]) then
            redis.call('HSET', KEYS[2], 'step', ARGV[1], 'token', ARGV[3])
        end
        return 1
        """;

    /// <inheritdoc />
    public async Task<RedisCheckpointHead?> HeadAsync(string headKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var db = multiplexer.GetDatabase(database);
        var marker = await db.HashGetAsync(headKey, [new RedisValue("step"), new RedisValue("token")]);
        return marker.Length == 2
               && marker[0].HasValue
               && marker[1].HasValue
               && long.TryParse((string?)marker[0], out var step)
            ? new RedisCheckpointHead(step, marker[1].ToString())
            : null;
    }

    /// <inheritdoc />
    public async Task BlindPutAsync(
        string threadKey,
        string headKey,
        long step,
        string json,
        string newToken,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var db = multiplexer.GetDatabase(database);
        await db.ScriptEvaluateAsync(
            BlindPutLua,
            [(RedisKey)threadKey, (RedisKey)headKey],
            new RedisValue[] { step, json, newToken });
    }

    /// <inheritdoc />
    public async Task<bool> GuardedPutAsync(
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
        cancellationToken.ThrowIfCancellationRequested();
        var db = multiplexer.GetDatabase(database);
        var result = await db.ScriptEvaluateAsync(
            GuardedPutLua,
            [(RedisKey)threadKey, (RedisKey)headKey],
            new RedisValue[]
            {
                step,
                newHeadStep,
                json,
                expectedStep,
                expectedToken ?? string.Empty,
                newToken,
            });
        return (int)result == 1;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RedisCheckpointEntry>> ListAsync(
        string threadKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var db = multiplexer.GetDatabase(database);
        var entries = await db.SortedSetRangeByRankWithScoresAsync(threadKey, order: Order.Ascending);
        return entries
            .Select(static entry => new RedisCheckpointEntry((long)entry.Score, entry.Element.ToString()))
            .ToArray();
    }

    /// <inheritdoc />
    public async Task<RedisCheckpointEntry?> LatestAsync(
        string threadKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var db = multiplexer.GetDatabase(database);
        var entries = await db.SortedSetRangeByRankWithScoresAsync(
            threadKey,
            start: 0,
            stop: 0,
            order: Order.Descending);
        return entries.Length == 0
            ? null
            : new RedisCheckpointEntry((long)entries[0].Score, entries[0].Element.ToString());
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListKeysAsync(string pattern, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var keys = new List<string>();
        foreach (var endpoint in multiplexer.GetEndPoints())
        {
            var server = multiplexer.GetServer(endpoint);
            await foreach (var key in server.KeysAsync(database, pattern: pattern, pageSize: 500)
                .WithCancellation(cancellationToken))
            {
                keys.Add(key.ToString());
            }
        }

        keys.Sort(StringComparer.Ordinal);
        return keys;
    }
}

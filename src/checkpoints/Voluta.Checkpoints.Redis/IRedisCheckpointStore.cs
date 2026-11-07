namespace Voluta.Checkpoints.Redis;

/// <summary>
///     Thin storage seam over Redis — real implementation talks to StackExchange.Redis;
///     unit tests substitute an in-memory store.
/// </summary>
/// <remarks>
///     Each thread is one sorted-set key: score = checkpoint step, member = wire JSON.
///     Guarded puts additionally CAS on a per-thread head marker (step + opaque token) so a
///     same-step lost update surfaces as a CAS loss instead of a silent overwrite. The token
///     is provider-local and never leaves this assembly.
/// </remarks>
internal interface IRedisCheckpointStore
{
    /// <summary>
    ///     Reads the thread's head marker, or <see langword="null" /> when the marker key is
    ///     absent (threads written before guarded puts existed).
    /// </summary>
    public Task<RedisCheckpointHead?> HeadAsync(string headKey, CancellationToken cancellationToken);

    /// <summary>
    ///     Blind put: upserts the JSON member under the given step score unconditionally
    ///     (last write wins, pre-0.5.0 behavior). When the head marker already exists and the
    ///     step advances it, the marker is refreshed with a fresh token; a marker-less thread
    ///     stays marker-less.
    /// </summary>
    public Task BlindPutAsync(
        string threadKey,
        string headKey,
        long step,
        string json,
        string newToken,
        CancellationToken cancellationToken);

    /// <summary>
    ///     Guarded put (atomic check-and-write in one primitive — Lua / equivalent). The CAS
    ///     compares the CURRENT marker against the writer's captured view:
    ///     <paramref name="expectedToken" /> non-null → marker must still be
    ///     (<paramref name="expectedStep" />, <paramref name="expectedToken" />);
    ///     <paramref name="expectedToken" /> null (legacy capture) → marker must still be
    ///     absent and the sorted set's latest score must equal
    ///     <paramref name="expectedStep" />. On success the member is written and the marker
    ///     is set to (<paramref name="newHeadStep" />, <paramref name="newToken" />) in the
    ///     same primitive. A CAS loss returns <see langword="false" /> (no write).
    /// </summary>
    public Task<bool> GuardedPutAsync(
        string threadKey,
        string headKey,
        long step,
        long newHeadStep,
        string json,
        long expectedStep,
        string? expectedToken,
        string newToken,
        CancellationToken cancellationToken);

    /// <summary>Returns all members ordered by step ascending; empty when the key is absent.</summary>
    public Task<IReadOnlyList<RedisCheckpointEntry>> ListAsync(string threadKey, CancellationToken cancellationToken);

    /// <summary>Returns the highest-step member or <see langword="null" /> when the key is absent.</summary>
    public Task<RedisCheckpointEntry?> LatestAsync(string threadKey, CancellationToken cancellationToken);

    /// <summary>SCAN-style key listing matching the given pattern (thread discovery).</summary>
    public Task<IReadOnlyList<string>> ListKeysAsync(string pattern, CancellationToken cancellationToken);
}

/// <summary>Per-thread head marker used as guarded-put CAS state.</summary>
/// <param name="Step">Latest step the marker has seen.</param>
/// <param name="Token">Provider-local opaque token, rotated on every guarded commit.</param>
internal sealed record RedisCheckpointHead(long Step, string Token);

/// <summary>One (step, json) pair read back from a thread sorted set.</summary>
/// <param name="Step">Checkpoint step (sorted-set score).</param>
/// <param name="Json">Wire-format JSON member.</param>
internal readonly record struct RedisCheckpointEntry(long Step, string Json);

using System.Text.Json;
using StackExchange.Redis;
using Voluta.Abstractions.Checkpoint;
using Voluta.Abstractions.Diagnostics;
using Voluta.Abstractions.Runtime;
using Voluta.Checkpoints.Redis.Wire;

namespace Voluta.Checkpoints.Redis;

/// <summary>
///     Redis checkpointer: one sorted set per thread (score = step, member = wire JSON).
/// </summary>
/// <remarks>
///     Key layout: <c>{prefix}thread:{safeThreadId}</c> (checkpoints, score = step) and
///     <c>{prefix}head:{safeThreadId}</c> (guarded-put head marker; never surfaced by
///     thread discovery).
///     Host registration: <c>v.Checkpoints.UseRedis(configure)</c> — requires
///     <see cref="IConnectionMultiplexer" /> registered in DI.
///     Direct construction is internal for conformance / unit tests only.
///     Channel values must be wire-format v1 allow-listed shapes; unsupported types fail Put with
///     <c>checkpoint.unsupported_value_type</c>.
///     Implements <see cref="IThreadDiscovery" /> via key scanning under the prefix.
/// </remarks>
public sealed class RedisCheckpointer : ICheckpointer, IThreadDiscovery
{
    private readonly IRedisCheckpointStore store;
    private readonly RedisCheckpointerOptions options;

    /// <summary>
    ///     Creates a Redis checkpointer over a live connection.
    /// </summary>
    /// <param name="multiplexer">Redis connection multiplexer.</param>
    /// <param name="options">Key prefix / database options.</param>
    internal RedisCheckpointer(IConnectionMultiplexer multiplexer, RedisCheckpointerOptions options)
        : this(new RedisCheckpointStore(multiplexer, options.Database), options)
    {
    }

    /// <summary>
    ///     Creates a Redis checkpointer over a substituted store (tests).
    /// </summary>
    /// <param name="store">Storage seam implementation.</param>
    /// <param name="options">Key prefix options.</param>
    internal RedisCheckpointer(IRedisCheckpointStore store, RedisCheckpointerOptions options)
    {
        this.store = store;
        this.options = options;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Blind write: unconditional sorted-set upsert (last write wins per step) — the
    ///     shipped 0.4.0 behavior. When a head marker already exists and the step advances it,
    ///     the marker is refreshed; marker-less threads stay marker-less.
    ///     <para>
    ///         RS0027: the guarded 4-parameter override deliberately takes ALL parameters as
    ///         required (no optional-parameter shadowing of this shipped form), so this shipped
    ///         overload is no longer the widest.
    ///     </para>
    /// </remarks>
    [global::System.Diagnostics.CodeAnalysis.SuppressMessage(
        "ApiDesign",
        "RS0027: API with optional parameter(s) should have the most parameters amongst its public overloads",
        Justification = "Shipped blind put is frozen; the guarded override takes all parameters as required.")]
    [global::System.Diagnostics.CodeAnalysis.SuppressMessage(
        "ApiDesign",
        "RS0027: API with optional parameter(s) should have the most parameters amongst its public overloads",
        Justification = "Shipped blind put is frozen; the guarded override takes all parameters as required.")]
    public Task PutAsync(
        CheckpointSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        return PutAsync(snapshot, expectedParentStep: null, unitOfWork: null, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Guarded put: the thread's head marker (step + provider-local opaque token) is
    ///     captured before the write, then the CAS compare-and-write — including the snapshot
    ///     member and the marker — runs in one atomic Lua script. A competing guarded put that
    ///     captured the same head loses with <c>checkpoint.conflict</c>, including same-step
    ///     rewrites. Threads without a marker derive the baseline from the sorted set and
    ///     create the marker on the first guarded commit (once-per-thread migration).
    /// </remarks>
    public async Task PutAsync(
        CheckpointSnapshot snapshot,
        long? expectedParentStep,
        ICheckpointUnitOfWork? unitOfWork,
        CancellationToken cancellationToken)
    {
        try
        {
            var document = RedisCheckpointDocument.FromSnapshot(snapshot);
            var json = JsonSerializer.Serialize(document, JsonSerializerOptions.Web);
            if (unitOfWork is not null)
            {
                // Transactional participation: the host owns the commit boundary — route the
                // write through its unit of work (explicit handoff, no ambient flow).
                await unitOfWork.ExecuteAsync(
                    token => PutCoreAsync(snapshot, json, expectedParentStep, token),
                    CancellationToken.None);
                return;
            }

            await PutCoreAsync(snapshot, json, expectedParentStep, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException
                                          and not CheckpointStoreException)
        {
            throw new CheckpointStoreException(
                VolutaErrorCodes.CheckpointPutFailed,
                $"Failed to put checkpoint for thread '{snapshot.ThreadId}' step {snapshot.Step}.",
                exception);
        }
    }

    private async Task PutCoreAsync(
        CheckpointSnapshot snapshot,
        string json,
        long? expectedParentStep,
        CancellationToken cancellationToken)
    {
        var threadKey = RedisCheckpointKeys.ThreadKey(options.KeyPrefix, snapshot.ThreadId);
        var headKey = RedisCheckpointKeys.HeadKey(options.KeyPrefix, snapshot.ThreadId);
        if (expectedParentStep is not { } expected)
        {
            await store.BlindPutAsync(
                threadKey,
                headKey,
                snapshot.Step,
                json,
                Guid.NewGuid().ToString("N"),
                cancellationToken);
            return;
        }

        // Capture the writer's view before the CAS script: the script re-verifies the marker
        // still matches, so a competing guarded put that captured the same head loses.
        var head = await store.HeadAsync(headKey, cancellationToken);
        long expectedStep;
        string? expectedToken;
        if (head is { } marker)
        {
            expectedStep = marker.Step;
            expectedToken = marker.Token;
        }
        else
        {
            // Legacy thread (no marker yet): baseline from the sorted set.
            var latest = await store.LatestAsync(threadKey, cancellationToken);
            if (latest is null)
            {
                throw new CheckpointStoreException(
                    VolutaErrorCodes.CheckpointConflict,
                    $"Checkpoint put for thread '{snapshot.ThreadId}' step {snapshot.Step} lost a " +
                    $"concurrency race: expected parent step {expected}, latest step <none>.");
            }

            expectedStep = latest.Value.Step;
            expectedToken = null;
        }

        if (expectedStep != expected)
        {
            throw new CheckpointStoreException(
                VolutaErrorCodes.CheckpointConflict,
                $"Checkpoint put for thread '{snapshot.ThreadId}' step {snapshot.Step} lost a " +
                $"concurrency race: expected parent step {expected}, latest step {expectedStep}.");
        }

        var written = await store.GuardedPutAsync(
            threadKey,
            headKey,
            snapshot.Step,
            Math.Max(expectedStep, snapshot.Step),
            json,
            expectedStep,
            expectedToken,
            Guid.NewGuid().ToString("N"),
            cancellationToken);
        if (!written)
        {
            throw new CheckpointStoreException(
                VolutaErrorCodes.CheckpointConflict,
                $"Checkpoint put for thread '{snapshot.ThreadId}' step {snapshot.Step} lost a " +
                $"concurrency race: expected parent step {expected} no longer matches " +
                "the thread's head.");
        }
    }

    /// <inheritdoc />
    public async Task<CheckpointSnapshot?> GetAsync(
        string threadId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var latest = await store.LatestAsync(
                RedisCheckpointKeys.ThreadKey(options.KeyPrefix, threadId),
                cancellationToken);
            return latest is { } entry ? Deserialize(entry.Json) : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException
                                          and not CheckpointStoreException)
        {
            throw new CheckpointStoreException(
                VolutaErrorCodes.CheckpointGetFailed,
                $"Failed to get checkpoint for thread '{threadId}'.",
                exception);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CheckpointSnapshot>> ListAsync(
        string threadId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var entries = await store.ListAsync(
                RedisCheckpointKeys.ThreadKey(options.KeyPrefix, threadId),
                cancellationToken);

            var snapshots = new List<CheckpointSnapshot>(entries.Count);
            foreach (var entry in entries.OrderBy(static item => item.Step))
            {
                if (TryDeserialize(entry.Json) is { } snapshot)
                {
                    snapshots.Add(snapshot);
                }
            }

            return snapshots;
        }
        catch (Exception exception) when (exception is not OperationCanceledException
                                          and not CheckpointStoreException)
        {
            throw new CheckpointStoreException(
                VolutaErrorCodes.CheckpointListFailed,
                $"Failed to list checkpoints for thread '{threadId}'.",
                exception);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Scans keys matching <c>{prefix}thread:*</c>; thread ids are the sanitized
    ///     segments used on Put.
    /// </remarks>
    public async Task<IReadOnlyList<string>> ListThreadIdsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var keys = await store.ListKeysAsync(
                RedisCheckpointKeys.ThreadScanPattern(options.KeyPrefix),
                cancellationToken);

            var ids = new List<string>();
            foreach (var key in keys)
            {
                if (RedisCheckpointKeys.TryParseThreadIdFromKey(key, options.KeyPrefix) is { } threadId)
                {
                    ids.Add(threadId);
                }
            }

            ids.Sort(StringComparer.Ordinal);
            return ids;
        }
        catch (Exception exception) when (exception is not OperationCanceledException
                                          and not CheckpointStoreException)
        {
            throw new CheckpointStoreException(
                VolutaErrorCodes.CheckpointListFailed,
                "Failed to list thread identifiers from the Redis checkpoint store.",
                exception);
        }
    }

    private static CheckpointSnapshot? TryDeserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<RedisCheckpointDocument>(json, JsonSerializerOptions.Web)
                ?.ToSnapshot();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static CheckpointSnapshot Deserialize(string json)
    {
        return TryDeserialize(json)
            ?? throw new CheckpointStoreException(
                VolutaErrorCodes.CheckpointCorruptPayload,
                "Redis checkpoint member could not be deserialized as a checkpoint.");
    }
}

using System.Net;
using System.Text;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using Voluta.Abstractions.Checkpoint;
using Voluta.Abstractions.Diagnostics;
using Voluta.Abstractions.Runtime;
using Voluta.Checkpoints.S3.Wire;

namespace Voluta.Checkpoints.S3;

/// <summary>
///     S3 (or S3-compatible) checkpointer: one object per (thread, step) under a key prefix.
/// </summary>
/// <remarks>
///     Key layout: <c>{prefix}/{safeThreadId}/{step:D12}.json</c>.
///     Host registration: <c>v.Checkpoints.UseS3(configure)</c>.
///     Direct construction is internal for conformance / unit tests only.
///     Channel values must be wire-format v1 allow-listed shapes; unsupported types fail Put with
///     <c>checkpoint.unsupported_value_type</c>.
///     Implements <see cref="IThreadDiscovery" /> via prefix listing with delimiter <c>/</c>.
/// </remarks>
public sealed class S3Checkpointer : ICheckpointer, IThreadDiscovery
{
    private readonly IAmazonS3 client;
    private readonly S3CheckpointerOptions options;
    private readonly string bucket;

    /// <summary>
    ///     Creates an S3 checkpointer.
    /// </summary>
    /// <param name="client">S3 client (must already be configured for the target endpoint).</param>
    /// <param name="options">Bucket and key-prefix options.</param>
    internal S3Checkpointer(IAmazonS3 client, S3CheckpointerOptions options)
    {
        this.client = client;
        this.options = options;
        bucket = InitBucket(options);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     <para>
    ///         Blind write: unconditional snapshot put; the latest-step pointer is refreshed
    ///         unconditionally but never regressed (max of the current and the written step) —
    ///         the shipped last-writer-wins behavior.
    ///     </para>
    ///     <para>
    ///         Guarded puts (<paramref name="expectedParentStep" /> non-null) are implemented as
    ///         a conditional write (S3 If-Match) on a per-thread latest-step pointer document,
    ///         performed BEFORE the snapshot object put: the pointer's ETag is read, and the
    ///         pointer is replaced only if it has not changed since — the atomic check-and-set.
    ///         A competing writer that lost the pointer race never writes its snapshot, so no
    ///         history is silently overwritten. When the pointer document is absent (threads
    ///         written before guarded puts existed), the guard falls back to enumerating the
    ///         thread's step documents and initializes the pointer with a conditional create
    ///         (If-None-Match: <c>*</c>) — a competing first guarded put loses with 412 →
    ///         <c>checkpoint.conflict</c> instead of silently double-initializing; after the
    ///         once-per-thread migration every put is conditional.
    ///     </para>
    ///     <para>
    ///         Backend note: If-Match / If-None-Match conditional puts require S3-compatible
    ///         storage that enforces write preconditions; the pointer write fails closed
    ///         (conflict) when the backend rejects the precondition. A pointer that is ahead of
    ///         the stored snapshots (crash between the two puts) also fails closed: subsequent
    ///         guarded puts conflict until a blind put re-baselines the pointer.
    ///     </para>
    /// </remarks>
    public Task PutAsync(
        CheckpointSnapshot snapshot,
        long? expectedParentStep,
        ICheckpointUnitOfWork? unitOfWork,
        CancellationToken cancellationToken)
    {
        try
        {
            var document = S3CheckpointDocument.FromSnapshot(snapshot);
            var json = JsonSerializer.Serialize(document, JsonSerializerOptions.Web);
            if (unitOfWork is not null)
            {
                // Transactional participation: the host owns the commit boundary — route the
                // write through its unit of work (explicit handoff, no ambient flow).
                return unitOfWork.ExecuteAsync(
                    token => PutCoreAsync(snapshot, json, expectedParentStep, token),
                    CancellationToken.None);
            }

            return PutCoreAsync(snapshot, json, expectedParentStep, cancellationToken);
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

    /// <inheritdoc />
    /// <remarks>
    ///     Blind write: unconditional snapshot put with an unconditional (never-regressing)
    ///     pointer refresh — the shipped 0.4.0 behavior.
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

    private async Task PutCoreAsync(
        CheckpointSnapshot snapshot,
        string json,
        long? expectedParentStep,
        CancellationToken cancellationToken)
    {
        var key = S3CheckpointKeys.ObjectKey(options.KeyPrefix, snapshot.ThreadId, snapshot.Step);
        var pointerKey = S3CheckpointKeys.PointerKey(options.KeyPrefix, snapshot.ThreadId);
        if (expectedParentStep is { } expected)
        {
            await GuardLatestStepAsync(snapshot, pointerKey, expected, cancellationToken);
        }

        await client.PutObjectAsync(
            new PutObjectRequest
            {
                BucketName = bucket,
                Key = key,
                ContentBody = json,
                ContentType = "application/json",
            },
            cancellationToken);

        if (expectedParentStep is null)
        {
            // Blind put: refresh the pointer unconditionally (last writer wins, as before) but
            // never regress it — an out-of-order blind put of a lower step must not break the
            // following guarded puts.
            var pointerStep = await GetPointerStepAsync(pointerKey, cancellationToken);
            await PutPointerAsync(
                pointerKey,
                Math.Max(pointerStep ?? snapshot.Step, snapshot.Step),
                etag: null,
                ifNoneMatch: false,
                cancellationToken);
        }
    }

    /// <summary>
    ///     Atomic check-and-set of the latest-step pointer (If-Match). Throws
    ///     <see cref="CheckpointStoreException" /> with <see cref="VolutaErrorCodes.CheckpointConflict" />
    ///     when another writer won the race or the expected parent step no longer matches.
    /// </summary>
    private async Task GuardLatestStepAsync(
        CheckpointSnapshot snapshot,
        string pointerKey,
        long expected,
        CancellationToken cancellationToken)
    {
        string? etag = null;
        long? pointerStep = null;
        try
        {
            using var pointer = await client.GetObjectAsync(
                new GetObjectRequest { BucketName = bucket, Key = pointerKey },
                cancellationToken);
            etag = pointer.ETag;
            using var reader = new StreamReader(pointer.ResponseStream, System.Text.Encoding.UTF8);
            var body = await reader.ReadToEndAsync(cancellationToken);
            pointerStep = long.TryParse(body.Trim(), out var parsed) ? parsed : null;
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            // Pointer absent (legacy thread): fall through to the enumerate-and-initialize path.
        }

        if (etag is not null)
        {
            if (pointerStep != expected)
            {
                throw Conflict(snapshot, expected, pointerStep);
            }

            // Conditional replace: succeeds only if no competing writer moved the pointer.
            // The pointer never regresses below its current step (same-step rewrite / fork).
            await PutPointerAsync(
                pointerKey,
                Math.Max(pointerStep!.Value, snapshot.Step),
                etag,
                ifNoneMatch: false,
                cancellationToken);
            return;
        }

        // Migration path: derive the latest step from the thread's step documents and create
        // the pointer with a conditional write (If-None-Match: *) — two racing first guarded
        // puts cannot both initialize; the loser surfaces checkpoint.conflict (fail closed).
        var latest = await GetLatestStepFromObjectsAsync(snapshot.ThreadId, cancellationToken);
        if (latest != expected)
        {
            throw Conflict(snapshot, expected, latest);
        }

        await PutPointerAsync(
            pointerKey,
            Math.Max(expected, snapshot.Step),
            etag: null,
            ifNoneMatch: true,
            cancellationToken);
    }

    /// <summary>Reads the pointer's step, or <see langword="null" /> when absent/unparseable.</summary>
    private async Task<long?> GetPointerStepAsync(string pointerKey, CancellationToken cancellationToken)
    {
        try
        {
            using var pointer = await client.GetObjectAsync(
                new GetObjectRequest { BucketName = bucket, Key = pointerKey },
                cancellationToken);
            using var reader = new StreamReader(pointer.ResponseStream, System.Text.Encoding.UTF8);
            var body = await reader.ReadToEndAsync(cancellationToken);
            return long.TryParse(body.Trim(), out var parsed) ? parsed : null;
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private async Task PutPointerAsync(
        string pointerKey,
        long step,
        string? etag,
        bool ifNoneMatch,
        CancellationToken cancellationToken)
    {
        var request = new PutObjectRequest
        {
            BucketName = bucket,
            Key = pointerKey,
            ContentBody = step.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ContentType = "text/plain",
        };
        if (etag is not null)
        {
            request.IfMatch = etag;
        }

        if (ifNoneMatch)
        {
            // Conditional create: fail with 412 when the pointer object already exists.
            request.IfNoneMatch = "*";
        }

        try
        {
            await client.PutObjectAsync(request, cancellationToken);
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            throw new CheckpointStoreException(
                VolutaErrorCodes.CheckpointConflict,
                $"Checkpoint put for thread '{request.Key}' lost a concurrency race: " +
                "the latest-step pointer changed before the conditional write landed.");
        }
    }

    private async Task<long?> GetLatestStepFromObjectsAsync(
        string threadId,
        CancellationToken cancellationToken)
    {
        var keys = await S3CheckpointListing.ListKeysAsync(
            client,
            bucket,
            S3CheckpointKeys.ThreadPrefix(options.KeyPrefix, threadId),
            cancellationToken);

        long? latest = null;
        foreach (var key in keys)
        {
            if (S3CheckpointKeys.TryParseStep(key, out var step)
                && (latest is null || step > latest.Value))
            {
                latest = step;
            }
        }

        return latest;
    }

    private static CheckpointStoreException Conflict(
        CheckpointSnapshot snapshot,
        long expected,
        long? actual)
    {
        return new CheckpointStoreException(
            VolutaErrorCodes.CheckpointConflict,
            $"Checkpoint put for thread '{snapshot.ThreadId}' step {snapshot.Step} lost a " +
            $"concurrency race: expected parent step {expected}, " +
            $"latest step {(actual is null ? "<none>" : actual.Value.ToString())}.");
    }

    /// <inheritdoc />
    public async Task<CheckpointSnapshot?> GetAsync(
        string threadId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var keys = await S3CheckpointListing.ListKeysAsync(
                client,
                bucket,
                S3CheckpointKeys.ThreadPrefix(options.KeyPrefix, threadId),
                cancellationToken);

            if (keys.Count == 0)
            {
                return null;
            }

            var latestKey = keys
                .Select(static key => (Key: key, Step: S3CheckpointKeys.TryParseStep(key, out var step) ? step : -1L))
                .Where(static pair => pair.Step >= 0)
                .OrderByDescending(static pair => pair.Step)
                .Select(static pair => pair.Key)
                .FirstOrDefault();

            return latestKey is null
                ? null
                : await S3CheckpointBody.GetSnapshotAsync(client, bucket, latestKey, cancellationToken);
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
            var keys = await S3CheckpointListing.ListKeysAsync(
                client,
                bucket,
                S3CheckpointKeys.ThreadPrefix(options.KeyPrefix, threadId),
                cancellationToken);

            var orderedKeys = keys
                .Select(static key => (Key: key, Step: S3CheckpointKeys.TryParseStep(key, out var step) ? step : -1L))
                .Where(static pair => pair.Step >= 0)
                .OrderBy(static pair => pair.Step)
                .Select(static pair => pair.Key)
                .ToArray();

            if (orderedKeys.Length == 0)
            {
                return [];
            }

            var snapshots = new CheckpointSnapshot[orderedKeys.Length];
            for (var index = 0; index < orderedKeys.Length; index++)
            {
                var snapshot = await S3CheckpointBody.GetSnapshotAsync(
                    client,
                    bucket,
                    orderedKeys[index],
                    cancellationToken);
                snapshots[index] = snapshot
                    ?? throw new CheckpointStoreException(
                        VolutaErrorCodes.CheckpointCorruptPayload,
                        $"S3 object '{orderedKeys[index]}' could not be deserialized as a checkpoint.");
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
    ///     Lists CommonPrefixes under the configured key prefix with delimiter <c>/</c>
    ///     (one prefix per thread directory). Thread ids are the sanitized segments used on Put.
    /// </remarks>
    public async Task<IReadOnlyList<string>> ListThreadIdsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var rootPrefix = S3CheckpointKeys.RootPrefix(options.KeyPrefix);
            var commonPrefixes = await S3CheckpointListing.ListCommonPrefixesAsync(
                client,
                bucket,
                rootPrefix,
                cancellationToken);

            var ids = new List<string>();
            foreach (var commonPrefix in commonPrefixes)
            {
                if (S3CheckpointKeys.TryParseThreadIdFromCommonPrefix(
                        commonPrefix,
                        rootPrefix,
                        out var threadId))
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
                "Failed to list thread identifiers from the S3 checkpoint store.",
                exception);
        }
    }

    private static string InitBucket(S3CheckpointerOptions checkpointerOptions)
    {
        return string.IsNullOrWhiteSpace(checkpointerOptions.BucketName)
            ? throw new ArgumentException("BucketName is required.", nameof(checkpointerOptions))
            : checkpointerOptions.BucketName;
    }
}

/// <summary>
///     List object keys / common prefixes under a prefix (paginated).
/// </summary>
file static class S3CheckpointListing
{
    public static async Task<IReadOnlyList<string>> ListKeysAsync(
        IAmazonS3 client,
        string bucket,
        string prefix,
        CancellationToken cancellationToken)
    {
        var keys = new List<string>();
        string? continuationToken = null;
        do
        {
            var response = await client.ListObjectsV2Async(
                new ListObjectsV2Request
                {
                    BucketName = bucket,
                    Prefix = prefix,
                    ContinuationToken = continuationToken,
                },
                cancellationToken);

            if (response.S3Objects is not null)
            {
                keys.AddRange(
                    response.S3Objects
                        .Select(static entry => entry.Key)
                        .Where(static key => !string.IsNullOrEmpty(key))!);
            }

            continuationToken = response.IsTruncated == true ? response.NextContinuationToken : null;
        }
        while (continuationToken is not null);

        return keys;
    }

    public static async Task<IReadOnlyList<string>> ListCommonPrefixesAsync(
        IAmazonS3 client,
        string bucket,
        string prefix,
        CancellationToken cancellationToken)
    {
        var prefixes = new List<string>();
        string? continuationToken = null;
        do
        {
            var response = await client.ListObjectsV2Async(
                new ListObjectsV2Request
                {
                    BucketName = bucket,
                    Prefix = prefix,
                    Delimiter = "/",
                    ContinuationToken = continuationToken,
                },
                cancellationToken);

            if (response.CommonPrefixes is not null)
            {
                prefixes.AddRange(
                    response.CommonPrefixes.Where(static entry => !string.IsNullOrEmpty(entry)));
            }

            continuationToken = response.IsTruncated == true ? response.NextContinuationToken : null;
        }
        while (continuationToken is not null);

        return prefixes;
    }
}

/// <summary>
///     Download and deserialize a checkpoint object body.
/// </summary>
file static class S3CheckpointBody
{
    public static async Task<CheckpointSnapshot?> GetSnapshotAsync(
        IAmazonS3 client,
        string bucket,
        string key,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetObjectAsync(
            new GetObjectRequest
            {
                BucketName = bucket,
                Key = key,
            },
            cancellationToken);

        await using var stream = response.ResponseStream;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var json = await reader.ReadToEndAsync(cancellationToken);
        var document = JsonSerializer.Deserialize<S3CheckpointDocument>(json, JsonSerializerOptions.Web);
        return document?.ToSnapshot();
    }
}

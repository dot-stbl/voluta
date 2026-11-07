using System.Collections.Concurrent;
using System.Text.Json;
using Voluta.Abstractions.Checkpoint;
using Voluta.Abstractions.Diagnostics;
using Voluta.Abstractions.Runtime;
using Voluta.Abstractions.Serialization;
using Voluta.Checkpoints.File.Wire;

namespace Voluta.Checkpoints.File;

/// <summary>
///     JSON file checkpointer: one directory per thread, history as step-ordered files.
/// </summary>
/// <remarks>
///     Host registration: <c>v.Checkpoints.UseFile(rootDirectory)</c> (or
///     <c>AddVolutaCheckpoints(c =&gt; c.UseFile(...))</c>). Direct construction is
///     internal for conformance / unit tests only.
///     Channel values must be wire-format v1 allow-listed shapes (primitives, string,
///     lists/dictionaries of those, JsonElement). Unsupported types fail Put with
///     <c>checkpoint.unsupported_value_type</c>.
///     Implements <see cref="IThreadDiscovery" /> by scanning child directories under the root.
///     Guarded puts CAS on a per-thread head marker file (<c>head.marker</c> inside the thread
///     directory — not a <c>*.json</c> snapshot document, so Get/List/step enumeration ignore
///     it) holding the head step plus a provider-local opaque token.
/// </remarks>
public sealed class FileCheckpointer : ICheckpointer, IThreadDiscovery, IRegistryAwareCheckpointer
{
    private const string HeadFileName = "head.marker";

    private readonly ConcurrentDictionary<string, object> locks = new(StringComparer.Ordinal);
    private readonly string root;

    /// <summary>Provider-local head marker: head step + opaque CAS token (never leaves the provider).</summary>
    private readonly record struct HeadMarker(long Step, string Token);

    /// <summary>
    ///     Creates a file checkpointer rooted at <paramref name="rootDirectory" />.
    /// </summary>
    /// <param name="rootDirectory">Root directory for per-thread checkpoint JSON files.</param>
    internal FileCheckpointer(string rootDirectory)
    {
        root = InitRoot(rootDirectory);
    }

    /// <inheritdoc />
    /// <summary>
    ///     Host type registry (v050, <see cref="IRegistryAwareCheckpointer" />): assigned by
    ///     the engine from <c>CompileOptions.CheckpointTypes</c>. Null — unstamped v1 behavior.
    /// </summary>
    public CheckpointTypeRegistry? TypeRegistry { get; set; }

    /// <inheritdoc />
    /// <remarks>
    ///     Blind write: unconditional (thread, step) document write — the shipped 0.4.0
    ///     behavior. When the head marker already exists it is refreshed (never regressed);
    ///     legacy threads without a marker stay marker-less.
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
        return PutCoreAsync(snapshot, expectedParentStep: null, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     <para>
    ///         The guard is a head-marker compare-and-set under the provider's in-process lock,
    ///         performed in two phases: the marker (step + opaque token) is captured before the
    ///         lock, and the commit verifies the marker is unchanged before writing the snapshot
    ///         and advancing the marker. A competing guarded put that captured the same head
    ///         therefore loses with <c>checkpoint.conflict</c> instead of silently overwriting —
    ///         including same-step rewrites. Threads written before guarded puts existed have no
    ///         marker: the baseline is derived from the step documents and the marker is created
    ///         on the first guarded commit. No cross-process atomicity exists (or is added) for
    ///         file storage.
    ///     </para>
    /// </remarks>
    public Task PutAsync(
        CheckpointSnapshot snapshot,
        long? expectedParentStep,
        ICheckpointUnitOfWork? unitOfWork,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (unitOfWork is not null)
        {
            // Transactional participation: the host owns the commit boundary — route the write
            // through its unit of work (explicit handoff, no ambient flow).
            return unitOfWork.ExecuteAsync(
                _ => PutCoreAsync(snapshot, expectedParentStep, CancellationToken.None),
                CancellationToken.None);
        }

        return PutCoreAsync(snapshot, expectedParentStep, cancellationToken);
    }

    private Task PutCoreAsync(
        CheckpointSnapshot snapshot,
        long? expectedParentStep,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (expectedParentStep is not { } expected)
        {
            PutBlindCore(snapshot);
            return Task.CompletedTask;
        }

        // Capture phase (before the commit lock): the writer's view of the head.
        var captured = TryReadHead(snapshot.ThreadId);
        long? baseline;
        string? capturedToken;
        if (captured is { } head)
        {
            baseline = head.Step;
            capturedToken = head.Token;
        }
        else
        {
            // Legacy thread (no marker yet): baseline from the step documents.
            baseline = GetLatestStep(snapshot.ThreadId);
            capturedToken = null;
        }

        if (baseline != expected)
        {
            throw Conflict(snapshot, expected, baseline);
        }

        var gate = locks.GetOrAdd(snapshot.ThreadId, static _ => new object());
        lock (gate)
        {
            // Commit phase: the marker must still match the captured view — otherwise another
            // guarded writer advanced the head between capture and commit (CAS loss).
            var current = TryReadHead(snapshot.ThreadId);
            if (current is { } currentHead)
            {
                if (currentHead.Step != baseline || currentHead.Token != capturedToken)
                {
                    throw Conflict(snapshot, expected, currentHead.Step);
                }
            }
            else
            {
                // Marker still absent: the documents must not have moved since the capture.
                var latest = GetLatestStep(snapshot.ThreadId);
                if (latest != baseline)
                {
                    throw Conflict(snapshot, expected, latest);
                }
            }

            WriteSnapshotLocked(snapshot);
            WriteHead(
                snapshot.ThreadId,
                Math.Max(expected, snapshot.Step),
                Guid.NewGuid().ToString("N"));
        }

        return Task.CompletedTask;
    }

    /// <summary>Blind path: snapshot write plus marker refresh (only when a marker already exists).</summary>
    private void PutBlindCore(CheckpointSnapshot snapshot)
    {
        var gate = locks.GetOrAdd(snapshot.ThreadId, static _ => new object());
        lock (gate)
        {
            WriteSnapshotLocked(snapshot);
            var head = TryReadHead(snapshot.ThreadId);
            if (head is { } marker && marker.Step < snapshot.Step)
            {
                // Keep the marker fresh for guarded puts; never regress it, never create one
                // on legacy threads (their first guarded put migrates from the documents).
                WriteHead(snapshot.ThreadId, snapshot.Step, Guid.NewGuid().ToString("N"));
            }
        }
    }

    /// <summary>Writes the step document (caller must hold the per-thread lock).</summary>
    private void WriteSnapshotLocked(CheckpointSnapshot snapshot)
    {
        var threadDir = FileCheckpointPaths.ThreadDirectory(root, snapshot.ThreadId);
        Directory.CreateDirectory(threadDir);
        var path = Path.Combine(threadDir, $"{snapshot.Step:D12}.json");
        var document = FileCheckpointDocument.FromSnapshot(snapshot, TypeRegistry);
        var json = JsonSerializer.Serialize(document, JsonSerializerOptions.Web);
        System.IO.File.WriteAllText(path, json);
    }

    private string HeadPath(string threadId)
    {
        return Path.Combine(FileCheckpointPaths.ThreadDirectory(root, threadId), HeadFileName);
    }

    private HeadMarker? TryReadHead(string threadId)
    {
        var path = HeadPath(threadId);
        if (!System.IO.File.Exists(path))
        {
            return null;
        }

        var lines = System.IO.File.ReadAllLines(path);
        return lines.Length == 2 && long.TryParse(lines[0], out var step)
            ? new HeadMarker(step, lines[1])
            : null;
    }

    private void WriteHead(string threadId, long step, string token)
    {
        var path = HeadPath(threadId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(
            path,
            $"{step.ToString(System.Globalization.CultureInfo.InvariantCulture)}\n{token}");
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

    /// <summary>Latest persisted step for a thread, or null when the thread has no documents.</summary>
    /// <remarks>Caller must hold the per-thread lock.</remarks>
    private long? GetLatestStep(string threadId)
    {
        var threadDir = FileCheckpointPaths.ThreadDirectory(root, threadId);
        if (!Directory.Exists(threadDir))
        {
            return null;
        }

        long? latest = null;
        foreach (var path in Directory.EnumerateFiles(threadDir, "*.json"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (long.TryParse(name, out var step)
                && (latest is null || step > latest.Value))
            {
                latest = step;
            }
        }

        return latest;
    }

    /// <inheritdoc />
    public Task<CheckpointSnapshot?> GetAsync(
        string threadId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var gate = locks.GetOrAdd(threadId, static _ => new object());
        lock (gate)
        {
            var threadDir = FileCheckpointPaths.ThreadDirectory(root, threadId);
            if (!Directory.Exists(threadDir))
            {
                return Task.FromResult<CheckpointSnapshot?>(null);
            }

            var latest = Directory.EnumerateFiles(threadDir, "*.json")
                .OrderByDescending(static path => path, StringComparer.Ordinal)
                .FirstOrDefault();
            if (latest is null)
            {
                return Task.FromResult<CheckpointSnapshot?>(null);
            }

            var json = System.IO.File.ReadAllText(latest);
            var document = JsonSerializer.Deserialize<FileCheckpointDocument>(json, JsonSerializerOptions.Web);
            return Task.FromResult(document?.ToSnapshot(TypeRegistry));
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<CheckpointSnapshot>> ListAsync(
        string threadId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var gate = locks.GetOrAdd(threadId, static _ => new object());
        lock (gate)
        {
            var threadDir = FileCheckpointPaths.ThreadDirectory(root, threadId);
            if (!Directory.Exists(threadDir))
            {
                return Task.FromResult<IReadOnlyList<CheckpointSnapshot>>([]);
            }

            var list = new List<CheckpointSnapshot>();
            foreach (var path in Directory.EnumerateFiles(threadDir, "*.json")
                         .OrderBy(static path => path, StringComparer.Ordinal))
            {
                var json = System.IO.File.ReadAllText(path);
                var document = JsonSerializer.Deserialize<FileCheckpointDocument>(json, JsonSerializerOptions.Web);
                if (document is not null)
                {
                    list.Add(document.ToSnapshot(TypeRegistry));
                }
            }

            return Task.FromResult<IReadOnlyList<CheckpointSnapshot>>(list);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Scans immediate child directories of the checkpointer root. Directory names are the
    ///     sanitized thread ids used on Put (invalid path chars replaced with <c>_</c>).
    /// </remarks>
    public Task<IReadOnlyList<string>> ListThreadIdsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(root))
        {
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        var ids = Directory.EnumerateDirectories(root)
            .Select(static path => Path.GetFileName(path))
            .Where(static name => !string.IsNullOrEmpty(name))
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
        return Task.FromResult<IReadOnlyList<string>>(ids!);
    }

    private static string InitRoot(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new ArgumentException("Root directory is required.", nameof(rootDirectory));
        }

        var full = Path.GetFullPath(rootDirectory);
        Directory.CreateDirectory(full);
        return full;
    }
}

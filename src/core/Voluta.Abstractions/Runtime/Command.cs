using Voluta.Abstractions.Channels;

namespace Voluta.Abstractions.Runtime;

/// <summary>
///     Resume input for an interrupted thread.
///     Prefer factories <c>Approve</c> / <c>Reject</c> / <c>Update</c> (and multi-interrupt
///     <c>ApproveResumes</c> / <c>RejectResumes</c> / <c>UpdateResumes</c>) over free-form
///     <see cref="Kind"/> strings; known kinds are
///     <see cref="Kinds.Approve"/>, <see cref="Kinds.Reject"/>, <see cref="Kinds.Update"/>.
/// </summary>
public sealed class Command
{
    /// <summary>
    ///     Closed set of resume kind labels for HITL (0.2 taxonomy).
    /// </summary>
    public static class Kinds
    {
        /// <summary>Human approved the interrupt; run continues with optional payload.</summary>
        public const string Approve = "approve";

        /// <summary>Human rejected the interrupt; reason is typically in <see cref="Payload"/>.</summary>
        public const string Reject = "reject";

        /// <summary>Apply channel writes (<see cref="Values"/>) then continue the interrupted node.</summary>
        public const string Update = "update";
    }

    /// <summary>
    ///     Kind label. Known values: <see cref="Kinds.Approve"/>, <see cref="Kinds.Reject"/>,
    ///     <see cref="Kinds.Update"/>. Unknown or empty kinds fail at resume validation.
    /// </summary>
    public string? Kind { get; init; }

    /// <summary>
    ///     Opaque resume payload injected as <c>GraphContext.ResumePayload</c> on the first
    ///     superstep after resume (approve/reject reason, free-form host data).
    ///     The answer, never the interrupt ask. A null payload still sets
    ///     <c>GraphContext.IsResume</c>; gate on that flag, not on payload null.
    /// </summary>
    public object? Payload { get; init; }

    /// <summary>
    ///     Optional channel-oriented values applied to the checkpoint state before the
    ///     interrupted node re-runs. Required (non-empty) when <see cref="Kind"/> is
    ///     <see cref="Kinds.Update"/>.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? Values { get; init; }

    /// <summary>
    ///     Per-task resume payloads keyed by interrupt/task id (multi-interrupt).
    ///     When set, each pending interrupt is resumed with the mapped payload.
    ///     Single-interrupt hosts may keep using <see cref="Payload" /> alone.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? Resumes { get; init; }

    /// <summary>
    ///     Builds an approve resume command.
    /// </summary>
    /// <param name="payload">Optional payload visible to the interrupted node on resume.</param>
    /// <param name="values">Optional channel merges applied before the node re-runs.</param>
    /// <returns>A command with <see cref="Kinds.Approve"/>.</returns>
    public static Command Approve(
        object? payload = null,
        IReadOnlyDictionary<string, object?>? values = null)
    {
        return new Command
        {
            Kind = Kinds.Approve,
            Payload = payload,
            Values = values,
        };
    }

    /// <summary>
    ///     Builds an approve resume that injects a payload per pending interrupt/task id.
    /// </summary>
    /// <param name="resumes">Map of task id → resume payload (must cover every pending interrupt).</param>
    /// <returns>A command with <see cref="Kinds.Approve"/> and <see cref="Resumes"/>.</returns>
    public static Command ApproveResumes(IReadOnlyDictionary<string, object?> resumes)
    {
        return new Command
        {
            Kind = Kinds.Approve,
            Resumes = resumes,
        };
    }

    /// <summary>
    ///     Builds an approve resume with per-task payloads and channel merges.
    /// </summary>
    /// <param name="resumes">Map of task id → resume payload.</param>
    /// <param name="values">Channel merges applied before interrupted tasks re-run.</param>
    /// <returns>A command with <see cref="Kinds.Approve"/> and <see cref="Resumes"/>.</returns>
    public static Command ApproveResumes(
        IReadOnlyDictionary<string, object?> resumes,
        IReadOnlyDictionary<string, object?> values)
    {
        return new Command
        {
            Kind = Kinds.Approve,
            Resumes = resumes,
            Values = values,
        };
    }

    /// <summary>
    ///     Builds a reject resume command. The interrupted node decides terminal vs re-route
    ///     from <see cref="Payload"/> (reason); the runtime does not auto-fail the thread.
    /// </summary>
    /// <param name="reason">Optional rejection reason (stored as <see cref="Payload"/>).</param>
    /// <param name="values">Optional channel merges applied before the node re-runs.</param>
    /// <returns>A command with <see cref="Kinds.Reject"/>.</returns>
    public static Command Reject(
        object? reason = null,
        IReadOnlyDictionary<string, object?>? values = null)
    {
        return new Command
        {
            Kind = Kinds.Reject,
            Payload = reason,
            Values = values,
        };
    }

    /// <summary>
    ///     Builds a reject resume that injects a reason per pending interrupt/task id.
    /// </summary>
    /// <param name="resumes">Map of task id → reject reason/payload.</param>
    /// <returns>A command with <see cref="Kinds.Reject"/> and <see cref="Resumes"/>.</returns>
    public static Command RejectResumes(IReadOnlyDictionary<string, object?> resumes)
    {
        return new Command
        {
            Kind = Kinds.Reject,
            Resumes = resumes,
        };
    }

    /// <summary>
    ///     Builds a reject resume with per-task reasons and channel merges.
    /// </summary>
    /// <param name="resumes">Map of task id → reject reason/payload.</param>
    /// <param name="values">Channel merges applied before interrupted tasks re-run.</param>
    /// <returns>A command with <see cref="Kinds.Reject"/> and <see cref="Resumes"/>.</returns>
    public static Command RejectResumes(
        IReadOnlyDictionary<string, object?> resumes,
        IReadOnlyDictionary<string, object?> values)
    {
        return new Command
        {
            Kind = Kinds.Reject,
            Resumes = resumes,
            Values = values,
        };
    }

    /// <summary>
    ///     Builds an update resume that merges channel values then re-runs the interrupted node.
    /// </summary>
    /// <param name="values">Non-empty channel map to apply before resume.</param>
    /// <returns>A command with <see cref="Kinds.Update"/>.</returns>
    public static Command Update(IReadOnlyDictionary<string, object?> values)
    {
        return CreateUpdate(values, payload: null);
    }

    /// <summary>
    ///     Builds an update resume that merges channel values then re-runs the interrupted node.
    /// </summary>
    /// <param name="values">Non-empty channel map to apply before resume.</param>
    /// <param name="payload">Payload for the interrupted node.</param>
    /// <returns>A command with <see cref="Kinds.Update"/>.</returns>
    public static Command Update(IReadOnlyDictionary<string, object?> values, object? payload)
    {
        return CreateUpdate(values, payload);
    }

    /// <summary>
    ///     Builds an update resume from channel writes (last write wins per channel name).
    /// </summary>
    /// <param name="writes">Channel writes to merge before resume.</param>
    /// <returns>A command with <see cref="Kinds.Update"/>.</returns>
    public static Command Update(params ChannelWrite[] writes)
    {
        return CreateUpdate(ToValueMap(writes), payload: null);
    }

    /// <summary>
    ///     Builds an update resume from channel writes (last write wins per channel name).
    /// </summary>
    /// <param name="writes">Channel writes to merge before resume.</param>
    /// <returns>A command with <see cref="Kinds.Update"/>.</returns>
    public static Command Update(IEnumerable<ChannelWrite> writes)
    {
        return CreateUpdate(ToValueMap(writes), payload: null);
    }

    /// <summary>
    ///     Builds an update resume from channel writes plus a resume payload.
    /// </summary>
    /// <param name="writes">Channel writes to merge before resume.</param>
    /// <param name="payload">Payload for the interrupted node.</param>
    /// <returns>A command with <see cref="Kinds.Update"/>.</returns>
    public static Command Update(IEnumerable<ChannelWrite> writes, object? payload)
    {
        return CreateUpdate(ToValueMap(writes), payload);
    }

    /// <summary>
    ///     Builds an update resume with per-task resume payloads (multi-interrupt).
    /// </summary>
    /// <param name="values">Non-empty channel map to apply before resume.</param>
    /// <param name="resumes">Map of task id → resume payload for each pending interrupt.</param>
    /// <returns>A command with <see cref="Kinds.Update"/> and <see cref="Resumes"/>.</returns>
    public static Command UpdateResumes(
        IReadOnlyDictionary<string, object?> values,
        IReadOnlyDictionary<string, object?> resumes)
    {
        return new Command
        {
            Kind = Kinds.Update,
            Values = values,
            Resumes = resumes,
        };
    }

    private static Command CreateUpdate(
        IReadOnlyDictionary<string, object?> values,
        object? payload)
    {
        return new Command
        {
            Kind = Kinds.Update,
            Payload = payload,
            Values = values,
        };
    }

    private static Dictionary<string, object?> ToValueMap(IEnumerable<ChannelWrite> writes)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var write in writes)
        {
            map[write.ChannelName] = write.Value;
        }

        return map;
    }

    /// <summary>
    ///     Returns whether <paramref name="kind"/> is one of the closed taxonomy labels
    ///     (ordinal, case-sensitive).
    /// </summary>
    /// <param name="kind">Kind string to test.</param>
    /// <returns><see langword="true"/> for approve, reject, or update.</returns>
    public static bool IsKnownKind(string? kind)
    {
        return kind is Kinds.Approve or Kinds.Reject or Kinds.Update;
    }
}

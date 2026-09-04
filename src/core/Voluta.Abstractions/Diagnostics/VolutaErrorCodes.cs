namespace Voluta.Abstractions.Diagnostics;

/// <summary>
///     Stable machine-readable error codes (dot.case) for <c>GraphException.Code</c>
///     and <c>CheckpointStoreException.Code</c>. Hosts branch on these strings;
///     see <c>VolutaEventIds</c> in the runtime package for matching MEL <c>EventId</c>s.
/// </summary>
/// <remarks>
///     Reserved codes for serde (#31) and command taxonomy (#32) are listed so hosts
///     can map them before throw sites land.
/// </remarks>
public static class VolutaErrorCodes
{
    /// <summary>Channel name is empty or whitespace at compile time.</summary>
    public const string GraphInvalidChannel = "graph.invalid_channel";

    /// <summary>Channel name registered twice.</summary>
    public const string GraphDuplicateChannel = "graph.duplicate_channel";

    /// <summary>Node name empty, whitespace, or a reserved sentinel.</summary>
    public const string GraphInvalidNode = "graph.invalid_node";

    /// <summary>Node name registered twice.</summary>
    public const string GraphDuplicateNode = "graph.duplicate_node";

    /// <summary>Edge endpoints empty or otherwise invalid.</summary>
    public const string GraphInvalidEdge = "graph.invalid_edge";

    /// <summary>Conditional router already registered for the source node.</summary>
    public const string GraphDuplicateConditional = "graph.duplicate_conditional";

    /// <summary>Compile with zero user nodes.</summary>
    public const string GraphNoNodes = "graph.no_nodes";

    /// <summary>No edge from START into the graph.</summary>
    public const string GraphMissingStart = "graph.missing_start";

    /// <summary>Edge or conditional targets an unknown node name.</summary>
    public const string GraphUnknownEndpoint = "graph.unknown_endpoint";

    /// <summary>Run exceeded <c>CompileOptions.RecursionLimit</c>.</summary>
    public const string GraphOutOfSteps = "graph.out_of_steps";

    /// <summary>Uncaught node fault or superstep failure.</summary>
    public const string GraphRunFailed = "graph.run_failed";

    /// <summary>Resume requested for a thread that is not interrupted.</summary>
    public const string GraphInvalidResume = "graph.invalid_resume";

    /// <summary>Continue requested for a thread that is not in Running status.</summary>
    public const string GraphInvalidContinue = "graph.invalid_continue";

    /// <summary>Thread id has no checkpoint (update / continue / fork source miss).</summary>
    public const string GraphThreadNotFound = "graph.thread_not_found";

    /// <summary>Requested history step is missing for the thread.</summary>
    public const string GraphStepNotFound = "graph.step_not_found";

    /// <summary>LastValue channel received multiple writers in one superstep.</summary>
    public const string ChannelConcurrentUpdate = "channel.concurrent_update";

    /// <summary>Custom reducer received a write it cannot merge.</summary>
    public const string ChannelInvalidWrite = "channel.invalid_write";

    /// <summary>Checkpointer Put failed (IO / provider).</summary>
    public const string CheckpointPutFailed = "checkpoint.put_failed";

    /// <summary>Checkpointer Get failed (IO / provider), not a miss.</summary>
    public const string CheckpointGetFailed = "checkpoint.get_failed";

    /// <summary>Checkpointer List failed (IO / provider).</summary>
    public const string CheckpointListFailed = "checkpoint.list_failed";

    /// <summary>Persisted payload could not be read (corrupt / null body).</summary>
    public const string CheckpointCorruptPayload = "checkpoint.corrupt_payload";

    /// <summary>Wire document version newer than this package supports.</summary>
    public const string CheckpointUnsupportedFormatVersion = "checkpoint.unsupported_format_version";

    /// <summary>Reserved (#31): channel value type cannot be serialized for the store.</summary>
    public const string CheckpointUnsupportedValueType = "checkpoint.unsupported_value_type";

    /// <summary>Reserved (#32): resume command kind is unknown or empty.</summary>
    public const string CommandInvalidKind = "command.invalid_kind";

    /// <summary>Reserved (#32): resume command payload shape is invalid for the kind.</summary>
    public const string CommandInvalidPayload = "command.invalid_payload";

    /// <summary>
    ///     All published codes (including reserved). Used by uniqueness tests and host docs.
    /// </summary>
    public static IReadOnlyList<string> All { get; } =
    [
        GraphInvalidChannel,
        GraphDuplicateChannel,
        GraphInvalidNode,
        GraphDuplicateNode,
        GraphInvalidEdge,
        GraphDuplicateConditional,
        GraphNoNodes,
        GraphMissingStart,
        GraphUnknownEndpoint,
        GraphOutOfSteps,
        GraphRunFailed,
        GraphInvalidResume,
        GraphInvalidContinue,
        GraphThreadNotFound,
        GraphStepNotFound,
        ChannelConcurrentUpdate,
        ChannelInvalidWrite,
        CheckpointPutFailed,
        CheckpointGetFailed,
        CheckpointListFailed,
        CheckpointCorruptPayload,
        CheckpointUnsupportedFormatVersion,
        CheckpointUnsupportedValueType,
        CommandInvalidKind,
        CommandInvalidPayload,
    ];
}

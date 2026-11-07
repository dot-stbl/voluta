namespace Voluta.Runtime.Engine.Tasks;

/// <summary>
///     One ready task for a superstep (PULL edge or PUSH/Send).
/// </summary>
internal sealed class ReadyTask(string nodeName, string taskId, object? taskPayload)
{
    public string NodeName { get; } = nodeName;

    public string TaskId { get; } = taskId;

    public object? TaskPayload { get; } = taskPayload;

    /// <summary>
    ///     Persisted retry attempt to run this task with on its first execution
    ///     (crash between attempts — snapshot's <c>TaskAttempts</c> records the next
    ///     attempt to execute). 0 = fresh task (attempt 1).
    /// </summary>
    public int StartAttempt { get; init; }
}

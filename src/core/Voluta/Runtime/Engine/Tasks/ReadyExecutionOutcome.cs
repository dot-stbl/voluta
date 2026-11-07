using Voluta.Exceptions;
using Voluta.Runtime.Engine.Support;

namespace Voluta.Runtime.Engine.Tasks;

/// <summary>
///     Outcome of attempting to execute the ready set.
/// </summary>
internal sealed class ReadyExecutionOutcome
{
    public bool Cancelled { get; init; }

    public Exception? Exception { get; init; }

    public GraphException? Failure { get; init; }

    public IReadOnlyList<NodeExecution>? Executions { get; init; }

    /// <summary>
    ///     Step-local durable progress buffer (per-task writes, completed ids, retry
    ///     attempts). Null only for cancelled/failure outcomes, where progress is dropped.
    /// </summary>
    public SuperstepProgress? Progress { get; init; }
}

using Voluta.Abstractions.Runtime;

namespace Voluta.Graph.Options;

/// <summary>
///     Per-node execution options passed to the new <c>AddNode(..., NodeOptions)</c>
///     builder overloads. Existing <c>AddNode</c> overloads keep their behavior
///     (no options = no retry).
/// </summary>
public sealed class NodeOptions
{
    /// <summary>
    ///     Retry policy for the node; <see langword="null" /> (default) = no retry
    ///     (a flaky failure fails the superstep as before).
    /// </summary>
    public RetryPolicy? Retry { get; init; }
}

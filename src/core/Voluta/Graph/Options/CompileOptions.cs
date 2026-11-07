using Voluta.Abstractions.Runtime;
using Voluta.Abstractions.Serialization;

namespace Voluta.Graph.Options;

/// <summary>
///     Options applied when compiling a <see cref="Builder.StateGraph" /> into a runnable graph.
/// </summary>
public sealed class CompileOptions
{
    /// <summary>
    ///     Maximum number of supersteps before the run fails with an out-of-steps error.
    ///     Defaults to 25 (binary compatibility with 0.4.0); pass
    ///     <see cref="RecursionLimit.Unlimited" /> (or <see cref="WithoutRecursionLimit" />)
    ///     to remove the superstep cap and rely on guards / terminal nodes instead.
    /// </summary>
    public int RecursionLimit { get; init; } = 25;

    /// <summary>
    ///     Optional host <see cref="IServiceProvider" /> exposed on every <see cref="GraphContext" />.
    ///     Required for <see cref="Builder.StateGraph.AddNode{TNode}(string)" /> and for
    ///     <see cref="GraphContext.GetRequiredService{T}" />.
    /// </summary>
    public IServiceProvider? Services { get; init; }

    /// <summary>
    ///     Host-assigned identity of this graph. Stamped onto every checkpoint snapshot the
    ///     compiled graph's runs write; resume / continue validate a snapshot's identity pair
    ///     against it (legacy snapshots without identity resume unchecked). Null — the graph is
    ///     anonymous (no identity fields on its snapshots).
    /// </summary>
    public string? GraphId { get; init; }

    /// <summary>
    ///     Host-assigned version paired with <see cref="GraphId" />. The runtime only compares
    ///     pairs for equality — any host versioning scheme (SemVer, monotonic ints, git sha) fits.
    /// </summary>
    public string? GraphVersion { get; init; }

    /// <summary>
    ///     Nodes to pause BEFORE entering (LangGraph InterruptBefore semantics). Every entry
    ///     to a superstep whose ready set contains one of these nodes parks the run with an
    ///     <c>Interrupted</c> snapshot (<see cref="RunStopReason.StaticBefore" />) before
    ///     executing the step. Unknown names fail at compile (<c>graph.unknown_endpoint</c>).
    /// </summary>
    public IReadOnlyList<string> InterruptBefore { get; init; } = [];

    /// <summary>
    ///     Nodes to pause AFTER output is applied and routed (LangGraph InterruptAfter
    ///     semantics). When one of these nodes executed in the superstep, the run parks with
    ///     an <c>Interrupted</c> snapshot (<see cref="RunStopReason.StaticAfter" />) carrying
    ///     the already-computed next ready set. Unknown names fail at compile.
    /// </summary>
    public IReadOnlyList<string> InterruptAfter { get; init; } = [];

    /// <summary>
    ///     Policy hooks evaluated before each superstep (before the recursion-limit and
    ///     static-pause checks); first Stop wins with a typed
    ///     <see cref="RunStopReason" /> (budget / deadline) on an <c>Interrupted</c> snapshot.
    /// </summary>
    public IReadOnlyList<IRunGuard> RunGuards { get; init; } = [];

    /// <summary>
    ///     Host type registry for provider wire stamping / typed restore. Travels
    ///     compile → topology → engine; durable providers implementing
    ///     <see cref="Voluta.Abstractions.Checkpoint.IRegistryAwareCheckpointer" /> receive it at run start.
    /// </summary>
    public CheckpointTypeRegistry? CheckpointTypes { get; init; }

    /// <summary>
    ///     Builds options with the superstep cap removed (<see cref="RecursionLimit.Unlimited" />).
    ///     Shipped default (25) stays the default for every other construction.
    /// </summary>
    /// <returns>Compile options without a superstep limit.</returns>
    public static CompileOptions WithoutRecursionLimit()
    {
        return new CompileOptions { RecursionLimit = global::Voluta.Graph.Options.RecursionLimit.Unlimited };
    }
}

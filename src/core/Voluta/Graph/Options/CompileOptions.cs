namespace Voluta.Graph.Options;

/// <summary>
///     Options applied when compiling a <see cref="Builder.StateGraph" /> into a runnable graph.
/// </summary>
public sealed class CompileOptions
{
    /// <summary>
    ///     Maximum number of supersteps before the run fails with an out-of-steps error.
    /// </summary>
    public int RecursionLimit { get; init; } = 25;

    /// <summary>
    ///     Optional host <see cref="IServiceProvider" /> exposed on every <see cref="GraphContext" />.
    ///     Required for <see cref="Builder.StateGraph.AddNode{TNode}" /> and for
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
}

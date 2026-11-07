using Voluta.Abstractions.Checkpoint;

namespace Voluta.Graph;

/// <summary>
///     Selects the executing graph per thread. Receives the thread id and the thread's latest
///     snapshot (or null for a thread that was never checkpointed) and returns the graph to run.
/// </summary>
/// <remarks>
///     <para>
///         Resolution contract: null snapshot → the host's default graph; a snapshot carrying a
///         graph identity pair (<see cref="CheckpointSnapshot.GraphId" /> /
///         <see cref="CheckpointSnapshot.GraphVersion" />) → the graph matching that pair.
///     </para>
///     <para>
///         The resolver only chooses the candidate — the engine still validates the snapshot's
///         pair against the returned graph before any node executes, so a mis-resolving resolver
///         surfaces <c>graph.version_mismatch</c> instead of silently running the wrong topology.
///     </para>
/// </remarks>
public interface IGraphResolver
{
    /// <summary>Resolves the graph to execute for a thread.</summary>
    /// <param name="threadId">Thread identifier.</param>
    /// <param name="snapshot">Latest snapshot for the thread, or null for a new thread.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    /// <returns>The graph to execute the run with.</returns>
    public Task<CompiledGraph> ResolveAsync(
        string threadId,
        CheckpointSnapshot? snapshot,
        CancellationToken cancellationToken = default);
}

/// <summary>
///     Resolver over a single compiled graph: every thread resolves to the fixed graph.
///     Identity validation stays with the engine (mismatching snapshots fail with
///     <c>graph.version_mismatch</c>). Registered automatically by
///     <c>VolutaBuilder.Graph(...)</c>; usable directly by hosts and tooling.
/// </summary>
public sealed class SingleGraphResolver(CompiledGraph graph) : IGraphResolver
{
    /// <inheritdoc />
    public Task<CompiledGraph> ResolveAsync(
        string threadId,
        CheckpointSnapshot? snapshot,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(graph);
    }
}

namespace Voluta.Abstractions.Runtime;

/// <summary>
///     Host-adapted transactional unit of work for checkpoint writes. A host wraps its own
///     storage transaction (e.g. its <c>DbContext</c>) so a checkpoint put and the host's own
///     work-item write commit — or roll back — together.
/// </summary>
/// <remarks>
///     <para>
///         Passed explicitly per run via <see cref="RunOptions.UnitOfWork" />; the runtime
///         carries it as run-scoped state and hands it to the checkpointer at put time.
///         Providers that understand the host's resource (located via <see cref="TryGetResource" />)
///         enlist in it; providers that do not fall back to a standalone write.
///     </para>
///     <para>
///         No ambient / <c>AsyncLocal</c> flow, deliberately: Voluta's execution surface is
///         async-iterator streams, where ambient flow is structurally unreliable (re-entrant
///         context is lost across <c>yield return</c> boundaries).
///     </para>
/// </remarks>
public interface ICheckpointUnitOfWork
{
    /// <summary>
    ///     Attempts to locate a host-owned resource inside this unit of work (for example the
    ///     host's <c>DbContext</c> instance a provider should enlist with).
    /// </summary>
    /// <typeparam name="T">Resource type the provider understands.</typeparam>
    /// <param name="resource">The located resource when found.</param>
    /// <returns><see langword="true" /> when the resource is part of this unit of work.</returns>
    public bool TryGetResource<T>(out T resource)
        where T : notnull;

    /// <summary>
    ///     Runs a storage write as part of this unit of work when a provider cannot adapt the
    ///     host's resource directly. The host remains the commit / rollback owner.
    /// </summary>
    /// <param name="write">Write to execute inside the host transaction.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    /// <returns>A task that completes when the write has been enlisted (not necessarily committed).</returns>
    public Task ExecuteAsync(Func<CancellationToken, Task> write, CancellationToken cancellationToken);
}

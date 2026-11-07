using Voluta.Abstractions.Runtime;

namespace Voluta.Testing.Checkpoint;

/// <summary>
///     No-op <see cref="ICheckpointUnitOfWork" /> double: exposes no host resources, writes
///     execute inline. Proves the explicit unit-of-work handoff reaches the checkpointer
///     without dragging host storage into the test.
/// </summary>
public sealed class NoopUnitOfWork : ICheckpointUnitOfWork
{
    /// <inheritdoc />
    public bool TryGetResource<T>(out T resource)
        where T : notnull
    {
        resource = default!;
        return false;
    }

    /// <inheritdoc />
    public Task ExecuteAsync(Func<CancellationToken, Task> write, CancellationToken cancellationToken)
    {
        return write(cancellationToken);
    }
}

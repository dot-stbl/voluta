using Voluta.Abstractions.Serialization;

namespace Voluta.Checkpoints.EntityFrameworkCore.Wire;

/// <summary>
///     Ambient flow for the EF provider's checkpoint type registry (v050).
/// </summary>
/// <remarks>
///     EF value converters are static expression trees — they cannot capture an instance
///     registry. The EF checkpointer instead sets <see cref="Current" /> around every
///     put / get / list so the <see cref="EfCheckpointDocument" /> conversion inside the
///     converter stamps registered types on encode and typed-restores them on decode.
///     <see cref="AsyncLocal{T}" /> keeps concurrent puts on different registry settings
///     isolated; unset (null) keeps unstamped v1 behavior byte-identical.
/// </remarks>
internal static class EfCheckpointRegistryScope
{
    private static readonly AsyncLocal<CheckpointTypeRegistry?> CurrentValue = new();

    /// <summary>The registry in scope for the current async flow (null — unstamped).</summary>
    public static CheckpointTypeRegistry? Current => CurrentValue.Value;

    /// <summary>
    ///     Runs <paramref name="action" /> with <paramref name="registry" /> in scope.
    /// </summary>
    public static async Task<T> RunAsync<T>(
        CheckpointTypeRegistry? registry,
        Func<Task<T>> action)
    {
        var previous = CurrentValue.Value;
        CurrentValue.Value = registry;
        try
        {
            return await action();
        }
        finally
        {
            CurrentValue.Value = previous;
        }
    }

    /// <summary>
    ///     Runs <paramref name="action" /> with <paramref name="registry" /> in scope.
    /// </summary>
    public static async Task RunAsync(
        CheckpointTypeRegistry? registry,
        Func<Task> action)
    {
        var previous = CurrentValue.Value;
        CurrentValue.Value = registry;
        try
        {
            await action();
        }
        finally
        {
            CurrentValue.Value = previous;
        }
    }
}

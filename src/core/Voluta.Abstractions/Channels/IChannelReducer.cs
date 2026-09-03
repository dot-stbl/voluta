namespace Voluta.Abstractions.Channels;

/// <summary>
///     Merges superstep writes into the current channel value.
///     Non-generic so the runtime store can keep <c>object?</c> values;
///     hosts that want a typed reducer wrap this contract.
/// </summary>
public interface IChannelReducer
{
    /// <summary>
    ///     Combines <paramref name="current" /> with ordered <paramref name="writes" />.
    /// </summary>
    /// <param name="current">Value after the previous apply, or <see langword="null" /> when empty.</param>
    /// <param name="writes">Writes for this channel in deterministic superstep order.</param>
    /// <returns>The reduced channel value.</returns>
    public object? Reduce(object? current, IReadOnlyList<object?> writes);
}

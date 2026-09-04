namespace Voluta.Abstractions.Channels.Reducers;

/// <summary>
///     Built-in reducer: at most one write per superstep; extra writes fail the run.
/// </summary>
public sealed class LastValueReducer() : IChannelReducer
{
    /// <inheritdoc />
    public object? Reduce(object? current, IReadOnlyList<object?> writes)
    {
        return writes.Count switch
        {
            0 => current,
            1 => writes[0],
            _ => throw new InvalidOperationException(
                "LastValue channel received more than one write in a single superstep."),
        };
    }
}

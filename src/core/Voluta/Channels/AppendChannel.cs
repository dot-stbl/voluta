using Voluta.Abstractions.Channels;
using Voluta.Abstractions.Channels.Reducers;

namespace Voluta.Channels;

/// <summary>
///     Channel that merges multiple superstep writes into an ordered list.
/// </summary>
internal sealed class AppendChannel : IChannel
{
    private static readonly AppendReducer Reducer = new();

    private object? value = new List<object?>();

    /// <inheritdoc />
    public ChannelKind Kind => ChannelKind.Append;

    /// <inheritdoc />
    public object? Get()
    {
        return value is List<object?> list ? list.ToList() : value;
    }

    /// <inheritdoc />
    public void Update(IReadOnlyList<object?> values)
    {
        value = Reducer.Reduce(value, values);
    }

    /// <inheritdoc />
    public void Restore(object? restored)
    {
        value = Reducer.Reduce(null, restored is null ? [] : [restored]);
    }
}

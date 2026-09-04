using Voluta.Abstractions.Channels;
using Voluta.Abstractions.Channels.Reducers;
using Voluta.Abstractions.Diagnostics;
using Voluta.Exceptions;

namespace Voluta.Channels.Custom;

/// <summary>
///     Channel that delegates superstep merge to a public <see cref="IChannelReducer" />.
/// </summary>
internal sealed class ReducerChannel(IChannelReducer reducer) : IChannel
{
    private object? value;

    /// <inheritdoc />
    public ChannelKind Kind => ChannelKind.LastValue;

    /// <inheritdoc />
    public object? Get()
    {
        return value;
    }

    /// <inheritdoc />
    public void Update(IReadOnlyList<object?> values)
    {
        try
        {
            value = reducer.Reduce(value, values);
        }
        catch (GraphException)
        {
            throw;
        }
        catch (InvalidOperationException exception) when (reducer is LastValueReducer)
        {
            throw new GraphConcurrentUpdateException(exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            throw new GraphException(VolutaErrorCodes.ChannelInvalidWrite, exception.Message, exception);
        }
    }

    /// <inheritdoc />
    public void Restore(object? restored)
    {
        value = restored;
    }
}

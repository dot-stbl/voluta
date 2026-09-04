using Voluta.Abstractions.Channels;
using Voluta.Abstractions.State;

namespace Voluta.Channels.Custom;

/// <summary>
///     Creates channel instances from declared kinds or custom reducers.
/// </summary>
internal static class ChannelFactory
{
    /// <summary>
    ///     Creates a fresh channel for the given kind.
    /// </summary>
    /// <param name="kind">Declared channel kind.</param>
    /// <returns>A new channel instance.</returns>
    public static IChannel Create(ChannelKind kind)
    {
        return kind switch
        {
            ChannelKind.LastValue => new LastValueChannel(),
            ChannelKind.Append => new AppendChannel(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown channel kind.")
        };
    }

    /// <summary>
    ///     Creates a channel from a compiled declaration (built-in kind or custom reducer).
    /// </summary>
    /// <param name="declaration">Channel name, kind, optional reducer and restore type.</param>
    /// <returns>A new channel instance.</returns>
    public static IChannel Create(GraphChannelDeclaration declaration)
    {
        return declaration.Reducer is { } reducer
            ? new ReducerChannel(reducer)
            : Create(declaration.Kind);
    }
}

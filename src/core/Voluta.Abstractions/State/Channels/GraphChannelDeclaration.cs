using Voluta.Abstractions.Channels;

namespace Voluta.Abstractions.State;

/// <summary>
///     One channel registration entry produced by hand or by source generation.
/// </summary>
/// <param name="name">Channel name in the graph state map.</param>
/// <param name="kind">Merge kind for multi-writer supersteps (Describe / built-in path).</param>
/// <param name="valueType">Optional CLR type used to coerce checkpoint restores.</param>
/// <param name="reducer">Optional custom reducer; when set, <paramref name="kind" /> is Describe-only.</param>
public sealed class GraphChannelDeclaration(
    string name,
    ChannelKind kind,
    Type? valueType,
    IChannelReducer? reducer)
{
    /// <summary>
    ///     Channel name, built-in kind, no restore type.
    /// </summary>
    /// <param name="name">Channel name in the graph state map.</param>
    /// <param name="kind">Merge kind for multi-writer supersteps.</param>
    public GraphChannelDeclaration(string name, ChannelKind kind)
        : this(name, kind, valueType: null, reducer: null)
    {
    }

    /// <summary>
    ///     Channel name, built-in kind, and restore type.
    /// </summary>
    /// <param name="name">Channel name in the graph state map.</param>
    /// <param name="kind">Merge kind for multi-writer supersteps.</param>
    /// <param name="valueType">CLR type used to coerce checkpoint restores.</param>
    public GraphChannelDeclaration(string name, ChannelKind kind, Type valueType)
        : this(name, kind, valueType, reducer: null)
    {
    }

    /// <summary>
    ///     Custom reducer channel without a restore type.
    ///     <see cref="Kind" /> is <see cref="ChannelKind.LastValue" /> for Describe.
    /// </summary>
    /// <param name="name">Channel name in the graph state map.</param>
    /// <param name="reducer">Custom merge reducer.</param>
    public GraphChannelDeclaration(string name, IChannelReducer reducer)
        : this(name, ChannelKind.LastValue, valueType: null, reducer)
    {
    }

    /// <summary>
    ///     Custom reducer channel with a restore type.
    ///     <see cref="Kind" /> is <see cref="ChannelKind.LastValue" /> for Describe.
    /// </summary>
    /// <param name="name">Channel name in the graph state map.</param>
    /// <param name="reducer">Custom merge reducer.</param>
    /// <param name="valueType">CLR type used to coerce checkpoint restores.</param>
    public GraphChannelDeclaration(string name, IChannelReducer reducer, Type? valueType)
        : this(name, ChannelKind.LastValue, valueType, reducer)
    {
    }

    /// <summary>
    ///     Channel name in the graph state map.
    /// </summary>
    public string Name { get; } = name;

    /// <summary>
    ///     Merge kind for multi-writer supersteps.
    ///     When <see cref="Reducer" /> is set this is a Describe placeholder
    ///     (<see cref="ChannelKind.LastValue" />) and is not used at apply time.
    /// </summary>
    public ChannelKind Kind { get; } = kind;

    /// <summary>
    ///     Declared value type for durable restore coercion, or <see langword="null" />
    ///     to keep checkpoint values as-is.
    /// </summary>
    public Type? ValueType { get; } = valueType;

    /// <summary>
    ///     Custom reducer, or <see langword="null" /> when <see cref="Kind" /> selects a built-in.
    /// </summary>
    public IChannelReducer? Reducer { get; } = reducer;
}

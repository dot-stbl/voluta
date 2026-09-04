namespace Voluta.Agents.AI;

/// <summary>
///     A function tool call extracted from a MEAI chat completion
///     (<see cref="Microsoft.Extensions.AI.FunctionCallContent" />).
/// </summary>
public sealed class ChatClientToolCall()
{
    /// <summary>
    ///     Function name requested by the model.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    ///     Function arguments as provided by the model, when present.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? Arguments { get; init; }

    /// <summary>
    ///     MEAI function-call id, when present (pairs with a later tool result).
    /// </summary>
    public string? CallId { get; init; }
}

namespace Voluta.Agents.AI;

/// <summary>
///     Token counts extracted from a MEAI chat completion
///     (<see cref="Microsoft.Extensions.AI.UsageDetails" />).
/// </summary>
public sealed class ChatClientUsage()
{
    /// <summary>
    ///     Prompt / input tokens (<c>UsageDetails.InputTokenCount</c>).
    /// </summary>
    public long? PromptTokens { get; init; }

    /// <summary>
    ///     Completion / output tokens (<c>UsageDetails.OutputTokenCount</c>).
    /// </summary>
    public long? CompletionTokens { get; init; }

    /// <summary>
    ///     Total tokens (<c>UsageDetails.TotalTokenCount</c>).
    /// </summary>
    public long? TotalTokens { get; init; }
}

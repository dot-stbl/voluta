using Voluta.Abstractions.Diagnostics;

namespace Voluta.Exceptions.Run;

/// <summary>
///     Out-of-band cancel was requested for a thread whose latest snapshot is terminal
///     (Done / Failed) and therefore cannot be cancelled.
/// </summary>
/// <remarks>
///     Initializes an invalid-cancel failure.
/// </remarks>
/// <param name="message">Human-readable description.</param>
public sealed class GraphInvalidCancelException(string message)
    : GraphException(VolutaErrorCodes.GraphInvalidCancel, message)
{
}

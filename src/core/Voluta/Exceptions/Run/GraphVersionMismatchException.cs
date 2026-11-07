using Voluta.Abstractions.Diagnostics;

namespace Voluta.Exceptions.Run;

/// <summary>
///     A thread's checkpoint identity pair does not match the identity of the graph that
///     would execute the run (resume / continue version validation).
/// </summary>
/// <remarks>
///     Initializes a version-mismatch failure.
/// </remarks>
/// <param name="message">Human-readable description.</param>
public sealed class GraphVersionMismatchException(string message)
    : GraphException(VolutaErrorCodes.GraphVersionMismatch, message)
{
}

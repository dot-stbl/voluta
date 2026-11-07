using Microsoft.AspNetCore.Http;
using Voluta.Exceptions;
using Voluta.Exceptions.Run;

namespace Voluta.UI;

/// <summary>
///     Maps <see cref="GraphException" /> subtypes onto stable JSON error responses
///     for the legacy ops API and the Studio v1 API.
/// </summary>
internal static class GraphExceptionResponse
{
    /// <summary>
    ///     Resolves an HTTP status for a graph exception; <see langword="null" /> when the
    ///     exception is not a mapped graph failure (let the host default handler run).
    /// </summary>
    /// <param name="exception">Exception thrown by the runtime.</param>
    /// <returns>HTTP status code or <see langword="null" />.</returns>
    internal static int? StatusFor(Exception exception)
    {
        return exception switch
        {
            GraphThreadNotFoundException => StatusCodes.Status404NotFound,
            GraphStepNotFoundException => StatusCodes.Status404NotFound,
            GraphInvalidResumeException => StatusCodes.Status409Conflict,
            GraphInvalidContinueException => StatusCodes.Status409Conflict,
            GraphVersionMismatchException => StatusCodes.Status409Conflict,
            GraphInvalidCancelException => StatusCodes.Status409Conflict,
            GraphInvalidCommandException => StatusCodes.Status400BadRequest,
            GraphOutOfStepsException => StatusCodes.Status409Conflict,
            _ => null,
        };
    }
}

using Voluta.Abstractions.Results;

namespace Voluta.Graph;

/// <summary>
///     DI-friendly node body: resolved from <see cref="GraphContext.Services" /> when registered via
///     <see cref="Builder.StateGraph.AddNode{TNode}(string)" />.
/// </summary>
public interface IGraphNode
{
    /// <summary>
    ///     Executes one node invocation for the current superstep.
    /// </summary>
    /// <param name="context">Frozen channel snapshot and optional DI services.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    /// <returns>Continue, interrupt, or other <see cref="NodeResult" />.</returns>
    public Task<NodeResult> InvokeAsync(GraphContext context, CancellationToken cancellationToken = default);
}

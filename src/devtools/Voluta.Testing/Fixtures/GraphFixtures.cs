using Voluta.Abstractions.Channels;
using Voluta.Abstractions.Results;
using Voluta.Graph;
using Voluta.Graph.Builder;
using Voluta.Graph.Options;

namespace Voluta.Testing.Fixtures;

/// <summary>
///     Small reusable <see cref="StateGraph" /> topologies for runtime unit tests.
/// </summary>
public static class GraphFixtures
{
    /// <summary>
    ///     START → single node → END with an Append channel.
    /// </summary>
    /// <param name="channelName">Channel name (default <c>messages</c>).</param>
    /// <param name="nodeName">Node name (default <c>a</c>).</param>
    /// <returns>Builder ready for <see cref="StateGraph.Compile" />.</returns>
    public static StateGraph Linear(string channelName = "messages", string nodeName = "a")
    {
        return new StateGraph()
            .AddChannel(channelName, ChannelKind.Append)
            .AddNode(
                nodeName,
                (context, _) =>
                {
                    return Task.FromResult<NodeResult>(
                        NodeResult.Continue(new ChannelWrite(channelName, $"from-{nodeName}")));
                })
            .AddEdge(GraphConstants.Start, nodeName)
            .AddEdge(nodeName, GraphConstants.End);
    }

    /// <summary>
    ///     START → loop → loop (self-edge). Pair with a low <see cref="CompileOptions.RecursionLimit" />.
    /// </summary>
    /// <param name="nodeName">Loop node name (default <c>loop</c>).</param>
    /// <returns>Builder ready for <see cref="StateGraph.Compile" />.</returns>
    public static StateGraph Cycle(string nodeName = "loop")
    {
        return new StateGraph()
            .AddChannel("n", ChannelKind.LastValue)
            .AddNode(
                nodeName,
                static (_, _) => Task.FromResult<NodeResult>(NodeResult.Continue()))
            .AddEdge(GraphConstants.Start, nodeName)
            .AddEdge(nodeName, nodeName);
    }

    /// <summary>
    ///     START → gate → END; gate interrupts when <see cref="GraphContext.IsResume" /> is false.
    /// </summary>
    /// <param name="channelName">Append channel for post-resume write (default <c>messages</c>).</param>
    /// <param name="nodeName">Gate node name (default <c>gate</c>).</param>
    /// <param name="interruptPayload">Payload stored on the interrupted checkpoint.</param>
    /// <returns>Builder ready for <see cref="StateGraph.Compile" />.</returns>
    public static StateGraph Interrupt(
        string channelName = "messages",
        string nodeName = "gate",
        object? interruptPayload = null)
    {
        var payload = interruptPayload ?? new { reason = "await-approval" };
        return new StateGraph()
            .AddChannel(channelName, ChannelKind.Append)
            .AddNode(
                nodeName,
                (context, _) =>
                {
                    return !context.IsResume
                        ? Task.FromResult<NodeResult>(NodeResult.Interrupt(payload))
                        : Task.FromResult<NodeResult>(
                            NodeResult.Continue(new ChannelWrite(channelName, "approved")));
                })
            .AddEdge(GraphConstants.Start, nodeName)
            .AddEdge(nodeName, GraphConstants.End);
    }

    /// <summary>
    ///     START fans out to two nodes that both write Append and then END (multi-ready superstep).
    /// </summary>
    /// <param name="channelName">Append channel (default <c>messages</c>).</param>
    /// <param name="leftName">Left node name (default <c>left</c>).</param>
    /// <param name="rightName">Right node name (default <c>right</c>).</param>
    /// <returns>Builder ready for <see cref="StateGraph.Compile" />.</returns>
    public static StateGraph MultiReady(
        string channelName = "messages",
        string leftName = "left",
        string rightName = "right")
    {
        return new StateGraph()
            .AddChannel(channelName, ChannelKind.Append)
            .AddNode(
                leftName,
                (_, _) => Task.FromResult<NodeResult>(
                    NodeResult.Continue(new ChannelWrite(channelName, "L"))))
            .AddNode(
                rightName,
                (_, _) => Task.FromResult<NodeResult>(
                    NodeResult.Continue(new ChannelWrite(channelName, "R"))))
            .AddEdge(GraphConstants.Start, leftName)
            .AddEdge(GraphConstants.Start, rightName)
            .AddEdge(leftName, GraphConstants.End)
            .AddEdge(rightName, GraphConstants.End);
    }
}

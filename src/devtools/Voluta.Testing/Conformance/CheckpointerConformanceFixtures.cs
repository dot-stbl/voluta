using Voluta.Abstractions.Checkpoint;
using Voluta.Abstractions.Runtime;

namespace Voluta.Testing.Conformance;

/// <summary>
///     Sample snapshots + equality checks for <see cref="CheckpointerConformance" />.
/// </summary>
internal static class CheckpointerConformanceFixtures
{
    public static CheckpointSnapshot CreateSampleSnapshot(
        string threadId,
        long step,
        GraphRunStatus status)
    {
        return new CheckpointSnapshot
        {
            ThreadId = threadId,
            Step = step,
            Status = status,
            ChannelValues = new Dictionary<string, object?>
            {
                ["messages"] = new List<object?> { "a" }
            },
            ChannelVersions = new Dictionary<string, long> { ["messages"] = 2 },
            VersionsSeen = new Dictionary<string, IReadOnlyDictionary<string, long>>
            {
                ["agent"] = new Dictionary<string, long> { ["messages"] = 1 }
            },
            PendingWrites =
            [
                new PendingWrite { TaskId = "agent", ChannelName = "messages", Value = "x" }
            ],
            LastNode = "agent",
            NextNodes = ["tools"],
            InterruptPayload = null
        };
    }

    public static void AssertEqual(CheckpointSnapshot expected, CheckpointSnapshot actual)
    {
        if (!string.Equals(actual.ThreadId, expected.ThreadId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Conformance: ThreadId mismatch (expected '{expected.ThreadId}', got '{actual.ThreadId}').");
        }

        if (actual.Step != expected.Step)
        {
            throw new InvalidOperationException(
                $"Conformance: Step mismatch (expected {expected.Step}, got {actual.Step}).");
        }

        if (actual.Status != expected.Status)
        {
            throw new InvalidOperationException(
                $"Conformance: Status mismatch (expected {expected.Status}, got {actual.Status}).");
        }

        if (actual.LastNode != expected.LastNode)
        {
            throw new InvalidOperationException(
                $"Conformance: LastNode mismatch (expected '{expected.LastNode}', got '{actual.LastNode}').");
        }

        if (!string.Equals(actual.GraphId, expected.GraphId, StringComparison.Ordinal)
            || !string.Equals(actual.GraphVersion, expected.GraphVersion, StringComparison.Ordinal)
            || !string.Equals(actual.CancelReason, expected.CancelReason, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Conformance: GraphId/GraphVersion/CancelReason mismatch after roundtrip.");
        }

        if (actual.NextNodes.Count != expected.NextNodes.Count
            || !actual.NextNodes.SequenceEqual(expected.NextNodes, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("Conformance: NextNodes mismatch after roundtrip.");
        }

        if (!actual.ChannelVersions.TryGetValue("messages", out var version) || version != 2)
        {
            throw new InvalidOperationException("Conformance: ChannelVersions['messages'] expected 2.");
        }

        if (!actual.VersionsSeen.TryGetValue("agent", out var seen)
            || !seen.TryGetValue("messages", out var seenVersion)
            || seenVersion != 1)
        {
            throw new InvalidOperationException("Conformance: VersionsSeen['agent']['messages'] expected 1.");
        }

        if (!actual.CompletedTaskIds.SequenceEqual(expected.CompletedTaskIds, StringComparer.Ordinal)
            || !actual.TaskAttempts.SequenceEqual(expected.TaskAttempts)
            || actual.JoinArrivals.Count != expected.JoinArrivals.Count
            || actual.StopReason != expected.StopReason)
        {
            throw new InvalidOperationException(
                "Conformance: v050 progress fields (CompletedTaskIds/TaskAttempts/JoinArrivals/StopReason) mismatch after roundtrip.");
        }
    }
}

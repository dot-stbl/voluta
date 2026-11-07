using Microsoft.Extensions.Logging;
using Shouldly;
using Voluta.Abstractions.Checkpoint;
using Voluta.Abstractions.Diagnostics;
using Voluta.Diagnostics;
using Voluta.Exceptions;
using Voluta.Exceptions.Run;
using Xunit;

namespace Voluta.Unit.Diagnostics;

public sealed class VolutaEventIdsShould
{
    [Fact(DisplayName = "Given the EventId catalog, when All is enumerated, then ids and names are unique")]
    public void HaveUniqueIdsAndNames()
    {
        VolutaEventIds.All.ShouldNotBeEmpty();
        VolutaEventIds.All.Select(static eventId => eventId.Id).Distinct().Count()
            .ShouldBe(VolutaEventIds.All.Count);
        VolutaEventIds.All.Select(static eventId => eventId.Name).Distinct(StringComparer.Ordinal).Count()
            .ShouldBe(VolutaEventIds.All.Count);
    }

    [Fact(DisplayName = "Given EventIds and error codes, when compared, then catalogs align 1:1 by name")]
    public void AlignWithErrorCodes()
    {
        VolutaEventIds.All.Count.ShouldBe(VolutaErrorCodes.All.Count);

        var eventNames = VolutaEventIds.All
            .Select(static eventId => eventId.Name)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
        var codes = VolutaErrorCodes.All
            .OrderBy(static code => code, StringComparer.Ordinal)
            .ToArray();

        eventNames.ShouldBe(codes);
    }

    [Fact(DisplayName = "Given typed graph exceptions, when constructed, then Code matches catalog constants")]
    public void TypedExceptionsUseCatalogCodes()
    {
        new GraphOutOfStepsException(limit: 10, step: 11).Code.ShouldBe(VolutaErrorCodes.GraphOutOfSteps);
        new GraphRunFailedException("failed").Code.ShouldBe(VolutaErrorCodes.GraphRunFailed);
        new GraphInvalidResumeException("not interrupted").Code.ShouldBe(VolutaErrorCodes.GraphInvalidResume);
        new GraphInvalidContinueException("not running").Code.ShouldBe(VolutaErrorCodes.GraphInvalidContinue);
        new GraphVersionMismatchException("identity mismatch").Code.ShouldBe(VolutaErrorCodes.GraphVersionMismatch);
        new GraphInvalidCancelException("cannot cancel").Code.ShouldBe(VolutaErrorCodes.GraphInvalidCancel);
        new GraphThreadNotFoundException("missing").Code.ShouldBe(VolutaErrorCodes.GraphThreadNotFound);
        new GraphStepNotFoundException("missing step").Code.ShouldBe(VolutaErrorCodes.GraphStepNotFound);
        new GraphConcurrentUpdateException("two writers").Code.ShouldBe(VolutaErrorCodes.ChannelConcurrentUpdate);
        new GraphCompileException(VolutaErrorCodes.GraphMissingStart, "missing")
            .Code.ShouldBe(VolutaErrorCodes.GraphMissingStart);
    }

    [Fact(DisplayName = "Given a GraphException, when GetEventId is called, then returns the matching catalog EventId")]
    public void ResolveEventIdFromGraphException()
    {
        var exception = new GraphOutOfStepsException(limit: 5, step: 6);

        var eventId = VolutaExceptionLogging.GetEventId(exception);

        eventId.ShouldBe(VolutaEventIds.GraphOutOfSteps);
        eventId.Name.ShouldBe(exception.Code);
    }

    [Fact(DisplayName = "Given a CheckpointStoreException, when GetEventId is called, then returns the matching catalog EventId")]
    public void ResolveEventIdFromCheckpointException()
    {
        var exception = new CheckpointStoreException(
            VolutaErrorCodes.CheckpointPutFailed,
            "put failed");

        var eventId = VolutaExceptionLogging.GetEventId(exception);

        eventId.ShouldBe(VolutaEventIds.CheckpointPutFailed);
        eventId.Name.ShouldBe(VolutaErrorCodes.CheckpointPutFailed);
    }

    [Fact(DisplayName = "Given an unknown code, when GetEventId is called, then returns fallback id 2999")]
    public void FallbackForUnknownCode()
    {
        var eventId = VolutaExceptionLogging.GetEventId("host.custom_error");

        eventId.Id.ShouldBe(2999);
        eventId.Name.ShouldBe("host.custom_error");
    }

    [Fact(DisplayName = "Given empty code, when GetEventId is called, then returns voluta.unknown fallback")]
    public void FallbackForEmptyCode()
    {
        var eventId = VolutaExceptionLogging.GetEventId(string.Empty);

        eventId.Id.ShouldBe(2999);
        eventId.Name.ShouldBe("voluta.unknown");
    }

    [Theory(DisplayName = "Given known GraphException subtypes, when GetEventId is called, then names match codes")]
    [MemberData(nameof(GraphExceptionCases))]
    public void ResolveEventIdForKnownGraphExceptionSubtypes(GraphException exception, EventId expected)
    {
        var eventId = VolutaExceptionLogging.GetEventId(exception);

        eventId.ShouldBe(expected);
        eventId.Name.ShouldBe(exception.Code);
        VolutaErrorCodes.All.ShouldContain(exception.Code);
    }

    [Fact(DisplayName = "Given command.invalid_kind, when GetEventId is called, then returns catalog EventId")]
    public void ResolveEventIdForCommandInvalidKind()
    {
        var exception = new GraphInvalidCommandException(VolutaErrorCodes.CommandInvalidKind, "bad kind");

        var eventId = VolutaExceptionLogging.GetEventId(exception);

        eventId.ShouldBe(VolutaEventIds.CommandInvalidKind);
        eventId.Name.ShouldBe(VolutaErrorCodes.CommandInvalidKind);
    }

    [Fact(DisplayName = "Given command.invalid_payload, when GetEventId is called, then returns catalog EventId")]
    public void ResolveEventIdForCommandInvalidPayload()
    {
        var exception = new GraphInvalidCommandException(VolutaErrorCodes.CommandInvalidPayload, "missing values");

        var eventId = VolutaExceptionLogging.GetEventId(exception);

        eventId.ShouldBe(VolutaEventIds.CommandInvalidPayload);
        eventId.Name.ShouldBe(VolutaErrorCodes.CommandInvalidPayload);
    }

    public static TheoryData<GraphException, EventId> GraphExceptionCases =>
        new()
        {
            {
                new GraphOutOfStepsException(limit: 2, step: 3),
                VolutaEventIds.GraphOutOfSteps
            },
            {
                new GraphRunFailedException("failed"),
                VolutaEventIds.GraphRunFailed
            },
            {
                new GraphInvalidResumeException("not interrupted"),
                VolutaEventIds.GraphInvalidResume
            },
            {
                new GraphInvalidContinueException("not running"),
                VolutaEventIds.GraphInvalidContinue
            },
            {
                new GraphVersionMismatchException("identity mismatch"),
                VolutaEventIds.GraphVersionMismatch
            },
            {
                new GraphInvalidCancelException("cannot cancel"),
                VolutaEventIds.GraphInvalidCancel
            },
            {
                new GraphThreadNotFoundException("missing"),
                VolutaEventIds.GraphThreadNotFound
            },
            {
                new GraphStepNotFoundException("missing step"),
                VolutaEventIds.GraphStepNotFound
            },
            {
                new GraphConcurrentUpdateException("two writers"),
                VolutaEventIds.ChannelConcurrentUpdate
            },
            {
                new GraphCompileException(VolutaErrorCodes.GraphNoNodes, "empty"),
                VolutaEventIds.GraphNoNodes
            },
            {
                new GraphCompileException(VolutaErrorCodes.GraphDuplicateNode, "dup"),
                VolutaEventIds.GraphDuplicateNode
            },
            {
                new GraphCompileException(VolutaErrorCodes.GraphUnknownEndpoint, "missing"),
                VolutaEventIds.GraphUnknownEndpoint
            },
            {
                new GraphCompileException(VolutaErrorCodes.GraphInvalidEdge, "end source"),
                VolutaEventIds.GraphInvalidEdge
            },
            {
                new GraphCompileException(VolutaErrorCodes.GraphMissingStart, "no start"),
                VolutaEventIds.GraphMissingStart
            },
            {
                new GraphInvalidCommandException(VolutaErrorCodes.CommandInvalidKind, "bad kind"),
                VolutaEventIds.CommandInvalidKind
            },
            {
                new GraphInvalidCommandException(VolutaErrorCodes.CommandInvalidPayload, "bad payload"),
                VolutaEventIds.CommandInvalidPayload
            },
        };
}

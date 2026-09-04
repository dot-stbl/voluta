using Microsoft.Extensions.Logging;
using Voluta.Abstractions.Diagnostics;

namespace Voluta.Diagnostics;

/// <summary>
///     MEL <see cref="EventId" /> catalog aligned 1:1 with <see cref="VolutaErrorCodes" />.
///     <see cref="EventId.Name" /> equals the stable code string; numeric ids are stable for log filters.
/// </summary>
public static class VolutaEventIds
{
    /// <summary><see cref="VolutaErrorCodes.GraphInvalidChannel" />.</summary>
    public static readonly EventId GraphInvalidChannel = new(2000, VolutaErrorCodes.GraphInvalidChannel);

    /// <summary><see cref="VolutaErrorCodes.GraphDuplicateChannel" />.</summary>
    public static readonly EventId GraphDuplicateChannel = new(2001, VolutaErrorCodes.GraphDuplicateChannel);

    /// <summary><see cref="VolutaErrorCodes.GraphInvalidNode" />.</summary>
    public static readonly EventId GraphInvalidNode = new(2002, VolutaErrorCodes.GraphInvalidNode);

    /// <summary><see cref="VolutaErrorCodes.GraphDuplicateNode" />.</summary>
    public static readonly EventId GraphDuplicateNode = new(2003, VolutaErrorCodes.GraphDuplicateNode);

    /// <summary><see cref="VolutaErrorCodes.GraphInvalidEdge" />.</summary>
    public static readonly EventId GraphInvalidEdge = new(2004, VolutaErrorCodes.GraphInvalidEdge);

    /// <summary><see cref="VolutaErrorCodes.GraphDuplicateConditional" />.</summary>
    public static readonly EventId GraphDuplicateConditional = new(2005, VolutaErrorCodes.GraphDuplicateConditional);

    /// <summary><see cref="VolutaErrorCodes.GraphNoNodes" />.</summary>
    public static readonly EventId GraphNoNodes = new(2006, VolutaErrorCodes.GraphNoNodes);

    /// <summary><see cref="VolutaErrorCodes.GraphMissingStart" />.</summary>
    public static readonly EventId GraphMissingStart = new(2007, VolutaErrorCodes.GraphMissingStart);

    /// <summary><see cref="VolutaErrorCodes.GraphUnknownEndpoint" />.</summary>
    public static readonly EventId GraphUnknownEndpoint = new(2008, VolutaErrorCodes.GraphUnknownEndpoint);

    /// <summary><see cref="VolutaErrorCodes.GraphOutOfSteps" />.</summary>
    public static readonly EventId GraphOutOfSteps = new(2020, VolutaErrorCodes.GraphOutOfSteps);

    /// <summary><see cref="VolutaErrorCodes.GraphRunFailed" />.</summary>
    public static readonly EventId GraphRunFailed = new(2021, VolutaErrorCodes.GraphRunFailed);

    /// <summary><see cref="VolutaErrorCodes.GraphInvalidResume" />.</summary>
    public static readonly EventId GraphInvalidResume = new(2022, VolutaErrorCodes.GraphInvalidResume);

    /// <summary><see cref="VolutaErrorCodes.GraphInvalidContinue" />.</summary>
    public static readonly EventId GraphInvalidContinue = new(2023, VolutaErrorCodes.GraphInvalidContinue);

    /// <summary><see cref="VolutaErrorCodes.GraphThreadNotFound" />.</summary>
    public static readonly EventId GraphThreadNotFound = new(2024, VolutaErrorCodes.GraphThreadNotFound);

    /// <summary><see cref="VolutaErrorCodes.GraphStepNotFound" />.</summary>
    public static readonly EventId GraphStepNotFound = new(2025, VolutaErrorCodes.GraphStepNotFound);

    /// <summary><see cref="VolutaErrorCodes.ChannelConcurrentUpdate" />.</summary>
    public static readonly EventId ChannelConcurrentUpdate = new(2040, VolutaErrorCodes.ChannelConcurrentUpdate);

    /// <summary><see cref="VolutaErrorCodes.ChannelInvalidWrite" />.</summary>
    public static readonly EventId ChannelInvalidWrite = new(2041, VolutaErrorCodes.ChannelInvalidWrite);

    /// <summary><see cref="VolutaErrorCodes.CheckpointPutFailed" />.</summary>
    public static readonly EventId CheckpointPutFailed = new(2100, VolutaErrorCodes.CheckpointPutFailed);

    /// <summary><see cref="VolutaErrorCodes.CheckpointGetFailed" />.</summary>
    public static readonly EventId CheckpointGetFailed = new(2101, VolutaErrorCodes.CheckpointGetFailed);

    /// <summary><see cref="VolutaErrorCodes.CheckpointListFailed" />.</summary>
    public static readonly EventId CheckpointListFailed = new(2102, VolutaErrorCodes.CheckpointListFailed);

    /// <summary><see cref="VolutaErrorCodes.CheckpointCorruptPayload" />.</summary>
    public static readonly EventId CheckpointCorruptPayload = new(2103, VolutaErrorCodes.CheckpointCorruptPayload);

    /// <summary><see cref="VolutaErrorCodes.CheckpointUnsupportedFormatVersion" />.</summary>
    public static readonly EventId CheckpointUnsupportedFormatVersion =
        new(2104, VolutaErrorCodes.CheckpointUnsupportedFormatVersion);

    /// <summary><see cref="VolutaErrorCodes.CheckpointUnsupportedValueType" /> (reserved #31).</summary>
    public static readonly EventId CheckpointUnsupportedValueType =
        new(2105, VolutaErrorCodes.CheckpointUnsupportedValueType);

    /// <summary><see cref="VolutaErrorCodes.CommandInvalidKind" /> (reserved #32).</summary>
    public static readonly EventId CommandInvalidKind = new(2300, VolutaErrorCodes.CommandInvalidKind);

    /// <summary><see cref="VolutaErrorCodes.CommandInvalidPayload" /> (reserved #32).</summary>
    public static readonly EventId CommandInvalidPayload = new(2301, VolutaErrorCodes.CommandInvalidPayload);

    /// <summary>All catalog EventIds (including reserved).</summary>
    public static IReadOnlyList<EventId> All { get; } =
    [
        GraphInvalidChannel,
        GraphDuplicateChannel,
        GraphInvalidNode,
        GraphDuplicateNode,
        GraphInvalidEdge,
        GraphDuplicateConditional,
        GraphNoNodes,
        GraphMissingStart,
        GraphUnknownEndpoint,
        GraphOutOfSteps,
        GraphRunFailed,
        GraphInvalidResume,
        GraphInvalidContinue,
        GraphThreadNotFound,
        GraphStepNotFound,
        ChannelConcurrentUpdate,
        ChannelInvalidWrite,
        CheckpointPutFailed,
        CheckpointGetFailed,
        CheckpointListFailed,
        CheckpointCorruptPayload,
        CheckpointUnsupportedFormatVersion,
        CheckpointUnsupportedValueType,
        CommandInvalidKind,
        CommandInvalidPayload,
    ];
}

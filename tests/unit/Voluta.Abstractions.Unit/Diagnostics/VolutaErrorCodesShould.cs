using Shouldly;
using Voluta.Abstractions.Diagnostics;
using Xunit;

namespace Voluta.Abstractions.Unit.Diagnostics;

public sealed class VolutaErrorCodesShould
{
    [Fact(DisplayName = "Given the error catalog, when All is enumerated, then codes are unique and non-empty")]
    public void HaveUniqueNonEmptyCodes()
    {
        VolutaErrorCodes.All.ShouldNotBeEmpty();
        VolutaErrorCodes.All.ShouldAllBe(static code => !string.IsNullOrWhiteSpace(code));
        VolutaErrorCodes.All.Distinct(StringComparer.Ordinal).Count().ShouldBe(VolutaErrorCodes.All.Count);
    }

    [Fact(DisplayName = "Given published codes, when inspected, then each uses dot.case segments")]
    public void UseDotCaseSegments()
    {
        foreach (var code in VolutaErrorCodes.All)
        {
            code.ShouldContain(".");
            code.ShouldNotContain(" ");
            code.ShouldBe(code.ToLowerInvariant());
        }
    }

    [Theory(DisplayName = "Given a known failure class, when reading the const, then matches the published string")]
    [InlineData(nameof(VolutaErrorCodes.GraphInvalidChannel), "graph.invalid_channel")]
    [InlineData(nameof(VolutaErrorCodes.GraphDuplicateChannel), "graph.duplicate_channel")]
    [InlineData(nameof(VolutaErrorCodes.GraphInvalidNode), "graph.invalid_node")]
    [InlineData(nameof(VolutaErrorCodes.GraphDuplicateNode), "graph.duplicate_node")]
    [InlineData(nameof(VolutaErrorCodes.GraphInvalidEdge), "graph.invalid_edge")]
    [InlineData(nameof(VolutaErrorCodes.GraphDuplicateConditional), "graph.duplicate_conditional")]
    [InlineData(nameof(VolutaErrorCodes.GraphNoNodes), "graph.no_nodes")]
    [InlineData(nameof(VolutaErrorCodes.GraphMissingStart), "graph.missing_start")]
    [InlineData(nameof(VolutaErrorCodes.GraphUnknownEndpoint), "graph.unknown_endpoint")]
    [InlineData(nameof(VolutaErrorCodes.GraphOutOfSteps), "graph.out_of_steps")]
    [InlineData(nameof(VolutaErrorCodes.GraphRunFailed), "graph.run_failed")]
    [InlineData(nameof(VolutaErrorCodes.GraphInvalidResume), "graph.invalid_resume")]
    [InlineData(nameof(VolutaErrorCodes.GraphInvalidContinue), "graph.invalid_continue")]
    [InlineData(nameof(VolutaErrorCodes.GraphThreadNotFound), "graph.thread_not_found")]
    [InlineData(nameof(VolutaErrorCodes.GraphStepNotFound), "graph.step_not_found")]
    [InlineData(nameof(VolutaErrorCodes.ChannelConcurrentUpdate), "channel.concurrent_update")]
    [InlineData(nameof(VolutaErrorCodes.ChannelInvalidWrite), "channel.invalid_write")]
    [InlineData(nameof(VolutaErrorCodes.CheckpointPutFailed), "checkpoint.put_failed")]
    [InlineData(nameof(VolutaErrorCodes.CheckpointGetFailed), "checkpoint.get_failed")]
    [InlineData(nameof(VolutaErrorCodes.CheckpointListFailed), "checkpoint.list_failed")]
    [InlineData(nameof(VolutaErrorCodes.CheckpointCorruptPayload), "checkpoint.corrupt_payload")]
    [InlineData(nameof(VolutaErrorCodes.CheckpointUnsupportedFormatVersion), "checkpoint.unsupported_format_version")]
    [InlineData(nameof(VolutaErrorCodes.CheckpointUnsupportedValueType), "checkpoint.unsupported_value_type")]
    [InlineData(nameof(VolutaErrorCodes.CommandInvalidKind), "command.invalid_kind")]
    [InlineData(nameof(VolutaErrorCodes.CommandInvalidPayload), "command.invalid_payload")]
    public void ExposeStableLiterals(string fieldName, string expected)
    {
        var value = typeof(VolutaErrorCodes).GetField(fieldName)?.GetValue(null) as string;

        value.ShouldBe(expected);
        VolutaErrorCodes.All.ShouldContain(expected);
    }

    [Fact(DisplayName = "Given public const string fields, when enumerated, then each is listed in All exactly once")]
    public void PublicConstsMatchAllCatalog()
    {
        var fields = typeof(VolutaErrorCodes)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(static field => field is { IsLiteral: true, IsInitOnly: false } && field.FieldType == typeof(string))
            .Select(static field => (string)field.GetRawConstantValue()!)
            .OrderBy(static code => code, StringComparer.Ordinal)
            .ToArray();

        var all = VolutaErrorCodes.All.OrderBy(static code => code, StringComparer.Ordinal).ToArray();

        fields.ShouldBe(all);
        fields.Length.ShouldBe(VolutaErrorCodes.All.Count);
    }
}

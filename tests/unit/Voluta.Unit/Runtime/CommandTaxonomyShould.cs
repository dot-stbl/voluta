using Shouldly;
using Voluta.Abstractions.Channels;
using Voluta.Abstractions.Diagnostics;
using Voluta.Abstractions.Results;
using Voluta.Abstractions.Runtime;
using Voluta.Abstractions.Streaming;
using Voluta.Checkpoint;
using Voluta.Exceptions.Run;
using Voluta.Graph;
using Voluta.Graph.Builder;
using Voluta.Runtime;
using Xunit;

namespace Voluta.Unit.Runtime;

public sealed class CommandTaxonomyShould
{
    [Fact(DisplayName = "Given Command.Approve, when EnsureValid is called, then succeeds")]
    public void AcceptApproveFactory()
    {
        Should.NotThrow(static () => CommandTaxonomy.EnsureValid(Command.Approve("ok")));
    }

    [Fact(DisplayName = "Given Command.Reject, when EnsureValid is called, then succeeds")]
    public void AcceptRejectFactory()
    {
        Should.NotThrow(static () => CommandTaxonomy.EnsureValid(Command.Reject("no")));
    }

    [Fact(DisplayName = "Given Command.Update with values, when EnsureValid is called, then succeeds")]
    public void AcceptUpdateFactory()
    {
        Should.NotThrow(static () => CommandTaxonomy.EnsureValid(
            Command.Update(new Dictionary<string, object?> { ["decision"] = "go" })));
    }

    [Fact(DisplayName = "Given Command.Update from channel writes, when EnsureValid is called, then succeeds")]
    public void AcceptUpdateFromWrites()
    {
        Should.NotThrow(static () => CommandTaxonomy.EnsureValid(
            Command.Update(new ChannelWrite("decision", "go"))));
    }

    [Theory(DisplayName = "Given known Kind string, when IsKnownKind is called, then returns true")]
    [InlineData(Command.Kinds.Approve)]
    [InlineData(Command.Kinds.Reject)]
    [InlineData(Command.Kinds.Update)]
    public void RecognizeKnownKinds(string kind)
    {
        Command.IsKnownKind(kind).ShouldBeTrue();
    }

    [Theory(DisplayName = "Given unknown or empty Kind, when EnsureValid is called, then throws command.invalid_kind")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("continue")]
    [InlineData("APPROVE")]
    [InlineData("Approve")]
    public void RejectInvalidKinds(string? kind)
    {
        var command = new Command { Kind = kind, Payload = "x" };

        var exception = Should.Throw<GraphInvalidCommandException>(() => CommandTaxonomy.EnsureValid(command));

        exception.Code.ShouldBe(VolutaErrorCodes.CommandInvalidKind);
    }

    [Fact(DisplayName = "Given update without Values, when EnsureValid is called, then throws command.invalid_payload")]
    public void RejectUpdateWithoutValues()
    {
        var command = new Command { Kind = Command.Kinds.Update };

        var exception = Should.Throw<GraphInvalidCommandException>(() => CommandTaxonomy.EnsureValid(command));

        exception.Code.ShouldBe(VolutaErrorCodes.CommandInvalidPayload);
        exception.Message.ShouldContain("update");
    }

    [Fact(DisplayName = "Given update with empty Values, when EnsureValid is called, then throws command.invalid_payload")]
    public void RejectUpdateWithEmptyValues()
    {
        var command = new Command
        {
            Kind = Command.Kinds.Update,
            Values = new Dictionary<string, object?>(),
        };

        var exception = Should.Throw<GraphInvalidCommandException>(() => CommandTaxonomy.EnsureValid(command));

        exception.Code.ShouldBe(VolutaErrorCodes.CommandInvalidPayload);
    }

    [Fact(DisplayName = "Given interrupted thread, when Resume with Approve factory, then continues to End")]
    public async Task ResumeApproveFactoryContinues()
    {
        var checkpointer = new InMemoryCheckpointer();
        var graph = BuildGateGraph(checkpointer, static context => !context.IsResume
            ? NodeResult.Interrupt("wait")
            : NodeResult.Continue(new ChannelWrite("messages", $"approve={context.ResumePayload}")));

        var interrupted = await graph.InvokeAsync(
            [],
            new RunOptions { ThreadId = "tax-approve", StreamMode = StreamMode.Events });
        interrupted.Kind.ShouldBe(StreamEventKind.Interrupt);

        var terminal = await graph.ResumeInvokeAsync("tax-approve", Command.Approve("ok"));

        terminal.Kind.ShouldBe(StreamEventKind.End);
        var done = await checkpointer.GetAsync("tax-approve");
        done!.Status.ShouldBe(GraphRunStatus.Done);
        var messages = done.ChannelValues["messages"].ShouldBeOfType<List<object?>>();
        messages.ShouldContain("approve=ok");
    }

    [Fact(DisplayName = "Given interrupted thread, when Resume with Reject factory, then gate sees reject payload")]
    public async Task ResumeRejectFactoryDeliversReason()
    {
        var checkpointer = new InMemoryCheckpointer();
        var graph = BuildGateGraph(checkpointer, static context => !context.IsResume
            ? NodeResult.Interrupt("wait")
            : NodeResult.Continue(new ChannelWrite("messages", $"reject={context.ResumePayload}")));

        await graph.InvokeAsync(
            [],
            new RunOptions { ThreadId = "tax-reject", StreamMode = StreamMode.Events });

        var terminal = await graph.ResumeInvokeAsync("tax-reject", Command.Reject("policy"));

        terminal.Kind.ShouldBe(StreamEventKind.End);
        var done = await checkpointer.GetAsync("tax-reject");
        var messages = done!.ChannelValues["messages"].ShouldBeOfType<List<object?>>();
        messages.ShouldContain("reject=policy");
    }

    [Fact(DisplayName = "Given interrupted thread, when Resume with Update factory, then Values apply before gate")]
    public async Task ResumeUpdateFactoryAppliesValues()
    {
        var checkpointer = new InMemoryCheckpointer();
        var graph = new StateGraph()
            .AddChannel("messages", ChannelKind.Append)
            .AddChannel("decision", ChannelKind.LastValue)
            .AddNode(
                "gate",
                static (context, _) => !context.IsResume
                    ? Task.FromResult<NodeResult>(NodeResult.Interrupt("need-decision"))
                    : Task.FromResult<NodeResult>(
                        NodeResult.Continue(
                            new ChannelWrite(
                                "messages",
                                $"decision={context.Read<string>("decision") ?? "(none)"}"))))
            .AddEdge(GraphConstants.Start, "gate")
            .AddEdge("gate", GraphConstants.End)
            .Compile(checkpointer);

        await graph.InvokeAsync(
            [],
            new RunOptions { ThreadId = "tax-update", StreamMode = StreamMode.Events });

        var terminal = await graph.ResumeInvokeAsync(
            "tax-update",
            Command.Update([new ChannelWrite("decision", "go")], "ok"));

        terminal.Kind.ShouldBe(StreamEventKind.End);
        var done = await checkpointer.GetAsync("tax-update");
        done!.ChannelValues["decision"].ShouldBe("go");
        var messages = done.ChannelValues["messages"].ShouldBeOfType<List<object?>>();
        messages.ShouldContain("decision=go");
    }

    [Fact(DisplayName = "Given interrupted thread, when Resume with unknown Kind, then throws command.invalid_kind")]
    public async Task ResumeUnknownKindFails()
    {
        var checkpointer = new InMemoryCheckpointer();
        var graph = BuildGateGraph(checkpointer, static context => !context.IsResume
            ? NodeResult.Interrupt("wait")
            : NodeResult.Continue());

        await graph.InvokeAsync(
            [],
            new RunOptions { ThreadId = "tax-bad", StreamMode = StreamMode.Events });

        var exception = await Should.ThrowAsync<GraphInvalidCommandException>(async () =>
        {
            await graph.ResumeInvokeAsync("tax-bad", new Command { Kind = "continue", Payload = "x" });
        });

        exception.Code.ShouldBe(VolutaErrorCodes.CommandInvalidKind);
        var stillInterrupted = await checkpointer.GetAsync("tax-bad");
        stillInterrupted!.Status.ShouldBe(GraphRunStatus.Interrupted);
    }

    [Fact(DisplayName = "Given interrupted thread, when Resume with update and no Values, then throws command.invalid_payload")]
    public async Task ResumeUpdateWithoutValuesFails()
    {
        var checkpointer = new InMemoryCheckpointer();
        var graph = BuildGateGraph(checkpointer, static context => !context.IsResume
            ? NodeResult.Interrupt("wait")
            : NodeResult.Continue());

        await graph.InvokeAsync(
            [],
            new RunOptions { ThreadId = "tax-upd-empty", StreamMode = StreamMode.Events });

        var exception = await Should.ThrowAsync<GraphInvalidCommandException>(async () =>
        {
            await graph.ResumeInvokeAsync(
                "tax-upd-empty",
                new Command { Kind = Command.Kinds.Update, Payload = "x" });
        });

        exception.Code.ShouldBe(VolutaErrorCodes.CommandInvalidPayload);
    }

    [Fact(DisplayName = "Given Approve with null payload, when EnsureValid is called, then succeeds")]
    public void AcceptApproveWithNullPayload()
    {
        Should.NotThrow(static () => CommandTaxonomy.EnsureValid(Command.Approve()));
    }

    [Fact(DisplayName = "Given Reject with null reason, when EnsureValid is called, then succeeds")]
    public void AcceptRejectWithNullPayload()
    {
        Should.NotThrow(static () => CommandTaxonomy.EnsureValid(Command.Reject()));
    }

    [Theory(DisplayName = "Given case-variant kind, when IsKnownKind is called, then returns false")]
    [InlineData("APPROVE")]
    [InlineData("Approve")]
    [InlineData("REJECT")]
    [InlineData("Update")]
    [InlineData("")]
    [InlineData("continue")]
    public void RejectCaseVariantsAsUnknownKinds(string kind)
    {
        Command.IsKnownKind(kind).ShouldBeFalse();
    }

    [Fact(DisplayName = "Given interrupted thread resumed to Done, when Resume again, then throws invalid_resume")]
    public async Task DoubleResumeAfterDoneFails()
    {
        var checkpointer = new InMemoryCheckpointer();
        var graph = BuildGateGraph(checkpointer, static context => !context.IsResume
            ? NodeResult.Interrupt("wait")
            : NodeResult.Continue(new ChannelWrite("messages", "once")));

        await graph.InvokeAsync(
            [],
            new RunOptions { ThreadId = "tax-double", StreamMode = StreamMode.Events });

        var first = await graph.ResumeInvokeAsync("tax-double", Command.Approve("ok"));
        first.Kind.ShouldBe(StreamEventKind.End);

        var exception = await Should.ThrowAsync<GraphInvalidResumeException>(async () =>
        {
            await graph.ResumeInvokeAsync("tax-double", Command.Approve("again"));
        });

        exception.Code.ShouldBe("graph.invalid_resume");
        var done = await checkpointer.GetAsync("tax-double");
        done!.Status.ShouldBe(GraphRunStatus.Done);
    }

    [Fact(DisplayName = "Given Failed thread, when Resume with Approve, then throws invalid_resume not invalid_command")]
    public async Task ResumeFailedThreadWithApproveIsInvalidResume()
    {
        var checkpointer = new InMemoryCheckpointer();
        var graph = new StateGraph()
            .AddNode(
                "boom",
                static (_, _) => throw new InvalidOperationException("boom"))
            .AddEdge(GraphConstants.Start, "boom")
            .AddEdge("boom", GraphConstants.End)
            .Compile(checkpointer);

        await Should.ThrowAsync<GraphRunFailedException>(async () =>
        {
            await graph.InvokeAsync([], new RunOptions { ThreadId = "tax-failed" });
        });

        var exception = await Should.ThrowAsync<GraphInvalidResumeException>(async () =>
        {
            await graph.ResumeInvokeAsync("tax-failed", Command.Approve("ok"));
        });

        exception.Code.ShouldBe("graph.invalid_resume");
        exception.ShouldNotBeOfType<GraphInvalidCommandException>();
        var latest = await checkpointer.GetAsync("tax-failed");
        latest!.Status.ShouldBe(GraphRunStatus.Failed);
    }

    private static CompiledGraph BuildGateGraph(
        InMemoryCheckpointer checkpointer,
        Func<GraphContext, NodeResult> gate)
    {
        return new StateGraph()
            .AddChannel("messages", ChannelKind.Append)
            .AddNode(
                "gate",
                (context, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return Task.FromResult(gate(context));
                })
            .AddEdge(GraphConstants.Start, "gate")
            .AddEdge("gate", GraphConstants.End)
            .Compile(checkpointer);
    }
}

using Shouldly;
using Voluta.Testing.Checkpoint;
using Voluta.Abstractions.Runtime;
using Voluta.Abstractions.Streaming;
using Xunit;

namespace Voluta.Abstractions.Unit.Runtime;

public sealed class RunOptionsShould
{
    [Fact(DisplayName = "Given ThreadId only, when constructed, then StreamMode defaults to Updates")]
    public void DefaultStreamModeToUpdates()
    {
        var options = new RunOptions { ThreadId = "t-1" };

        options.ThreadId.ShouldBe("t-1");
        options.StreamMode.ShouldBe(StreamMode.Updates);
    }

    [Fact(DisplayName = "Given no unit of work, when constructed, then UnitOfWork defaults to null")]
    public void DefaultUnitOfWorkToNull()
    {
        var options = new RunOptions { ThreadId = "t-1" };

        options.UnitOfWork.ShouldBeNull();
    }

    [Fact(DisplayName = "Given a unit of work, when set, then the explicit instance is preserved (no ambient flow)")]
    public void PreserveExplicitUnitOfWork()
    {
        var unitOfWork = new NoopUnitOfWork();
        var options = new RunOptions { ThreadId = "t-1", UnitOfWork = unitOfWork };

        options.UnitOfWork.ShouldBeSameAs(unitOfWork);
    }

    [Fact(DisplayName = "Given Command fields, when constructed, then preserves kind, payload, and values")]
    public void CarryCommandFields()
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal) { ["approved"] = true };

        var command = new Command
        {
            Kind = "approve",
            Payload = "ok",
            Values = values,
        };

        command.Kind.ShouldBe("approve");
        command.Payload.ShouldBe("ok");
        command.Values.ShouldBeSameAs(values);
    }
}

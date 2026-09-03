using Shouldly;
using Voluta.Abstractions.Channels;
using Voluta.Abstractions.Results;
using Voluta.Abstractions.Runtime;
using Voluta.Abstractions.State;
using Voluta.Abstractions.Streaming;
using Voluta.Checkpoint;
using Voluta.Generators.Unit.Fixtures;
using Voluta.Graph.Builder;
using Xunit;

namespace Voluta.Generators.Unit;

public sealed class AgentStateGenerationShould
{
    [Fact(DisplayName = "Given Append Messages + LastValue Status, when CreateSchema is called, then both channels register")]
    public void CreateSchemaWithChannelKinds()
    {
        var schema = AgentState.CreateSchema();

        schema.Channels.Count.ShouldBe(2);
        schema.Channels[0].Name.ShouldBe(nameof(AgentState.Messages));
        schema.Channels[0].Kind.ShouldBe(ChannelKind.Append);
        schema.Channels[0].ValueType.ShouldBe(typeof(IList<object?>));
        schema.Channels[1].Name.ShouldBe(nameof(AgentState.Status));
        schema.Channels[1].Kind.ShouldBe(ChannelKind.LastValue);
        schema.Channels[1].ValueType.ShouldBe(typeof(string));
    }

    [Fact(DisplayName = "Given only Status set on Update, when ToWrites is called, then emits solely the status write")]
    public void ToWritesOmitsUnsetFields()
    {
        var update = new AgentState.AgentStateUpdate
        {
            Status = OptionalValue<string?>.Of("running"),
        };

        var writes = update.ToWrites();

        writes.Count.ShouldBe(1);
        writes[0].ChannelName.ShouldBe(nameof(AgentState.Status));
        writes[0].Value.ShouldBe("running");
    }

    [Fact(DisplayName = "Given explicit null Status, when ToWrites is called, then emits a clear write (null value)")]
    public void ToWritesEmitsExplicitNull()
    {
        var update = new AgentState.AgentStateUpdate
        {
            Status = OptionalValue<string?>.Of(null),
        };

        var writes = update.ToWrites();

        writes.Count.ShouldBe(1);
        writes[0].ChannelName.ShouldBe(nameof(AgentState.Status));
        writes[0].Value.ShouldBeNull();
    }

    [Fact(DisplayName = "Given both fields set, when ToWrites is called, then emits Messages and Status writes")]
    public void ToWritesEmitsAllSetFields()
    {
        IList<object?> messages = ["hello"];
        var update = new AgentState.AgentStateUpdate
        {
            Messages = OptionalValue<IList<object?>>.Of(messages),
            Status = OptionalValue<string?>.Of("done"),
        };

        var writes = update.ToWrites();

        writes.Count.ShouldBe(2);
        writes.ShouldContain(write => write.ChannelName == nameof(AgentState.Messages) && write.Value == messages);
        writes.ShouldContain(write => write.ChannelName == nameof(AgentState.Status) && Equals(write.Value, "done"));
    }

    [Fact(DisplayName = "Given schema from CreateSchema, when AddChannels is used, then graph compiles and applies typed ToWrites")]
    public async Task IntegrateSchemaWithStateGraph()
    {
        var checkpointer = new InMemoryCheckpointer();
        var graph = new StateGraph()
            .AddChannels(AgentState.CreateSchema())
            .AddNode(
                "set-status",
                static (context, _) =>
                {
                    var update = new AgentState.AgentStateUpdate
                    {
                        Status = OptionalValue<string?>.Of("from-node"),
                    };
                    return Task.FromResult<NodeResult>(NodeResult.Continue(update.ToWrites()));
                })
            .AddEdge(GraphConstants.Start, "set-status")
            .AddEdge("set-status", GraphConstants.End)
            .Compile(checkpointer);

        var terminal = await graph.InvokeAsync(
            [new ChannelWrite(nameof(AgentState.Status), "seed")],
            new RunOptions { ThreadId = "gen-1", StreamMode = StreamMode.Values });

        terminal.Kind.ShouldBe(StreamEventKind.End);
        terminal.State.ShouldNotBeNull();
        terminal.State![nameof(AgentState.Status)].ShouldBe("from-node");
    }

    [Fact(DisplayName = "Given hand-built GraphChannelSchema.Builder, when Build is called, then mirrors generator schema shape")]
    public void FluentSchemaBuilderMirrorsGeneratorShape()
    {
        var schema = new GraphChannelSchema.Builder()
            .Add(nameof(AgentState.Messages), ChannelKind.Append)
            .Add(nameof(AgentState.Status), ChannelKind.LastValue)
            .Build();

        schema.Channels.Count.ShouldBe(2);
        schema.Channels[0].Kind.ShouldBe(ChannelKind.Append);
        schema.Channels[1].Kind.ShouldBe(ChannelKind.LastValue);
    }
}

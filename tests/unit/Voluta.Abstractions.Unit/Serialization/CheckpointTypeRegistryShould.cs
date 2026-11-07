using Shouldly;
using Voluta.Abstractions.Serialization;
using Xunit;

namespace Voluta.Abstractions.Unit.Serialization;

public sealed class CheckpointTypeRegistryShould
{
    private sealed record Order(string Sku, int Qty);

    [Fact(DisplayName = "Given Empty registry, when queried, then resolves nothing")]
    public void EmptyResolvesNothing()
    {
        CheckpointTypeRegistry.Empty.TryGetTypeId(typeof(Order), out _).ShouldBeFalse();
        CheckpointTypeRegistry.Empty.TryResolveType("host:order", out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Given registration, when queried both directions, then ids and types resolve")]
    public void RegisterAndResolveBothDirections()
    {
        var registry = new CheckpointTypeRegistry()
            .Register<Order>("host:order")
            .Register(typeof(string), "host:text");

        registry.TryGetTypeId(typeof(Order), out var typeId).ShouldBeTrue();
        typeId.ShouldBe("host:order");
        registry.TryResolveType("host:order", out var type).ShouldBeTrue();
        type.ShouldBe(typeof(Order));
        registry.TryGetTypeId(typeof(string), out var textId).ShouldBeTrue();
        textId.ShouldBe("host:text");
        registry.TryResolveType("host:text", out var textType).ShouldBeTrue();
        textType.ShouldBe(typeof(string));
    }

    [Fact(DisplayName = "Given duplicate id with a different CLR type, when registered, then ArgumentException")]
    public void RejectDuplicateTypeIdWithDifferentType()
    {
        var registry = new CheckpointTypeRegistry().Register<Order>("host:dup");

        Should.Throw<ArgumentException>(
            () => registry.Register(typeof(string), "host:dup"));
    }

    [Fact(DisplayName = "Given duplicate CLR type with a different id, when registered, then ArgumentException")]
    public void RejectDuplicateTypeWithDifferentId()
    {
        var registry = new CheckpointTypeRegistry().Register<Order>("host:first");

        Should.Throw<ArgumentException>(
            () => registry.Register<Order>("host:second"));
    }

    [Fact(DisplayName = "Given same id and type re-registered, when registered, then fluent no-op (idempotent)")]
    public void AcceptIdempotentReRegistration()
    {
        var registry = new CheckpointTypeRegistry();

        Should.NotThrow(
            () => registry
                .Register<Order>("host:order")
                .Register(typeof(Order), "host:order"));

        registry.TryGetTypeId(typeof(Order), out var typeId).ShouldBeTrue();
        typeId.ShouldBe("host:order");
    }

    [Theory(DisplayName = "Given empty or oversized type id, when registered, then ArgumentException")]
    [InlineData(0)]
    [InlineData(129)]
    public void RejectInvalidTypeIds(int length)
    {
        var registry = new CheckpointTypeRegistry();
        var typeId = length == 0 ? string.Empty : new string('a', length);

        Should.Throw<ArgumentException>(() => registry.Register<Order>(typeId));
    }

    [Fact(DisplayName = "Given null arguments, when registered, then ArgumentException")]
    public void RejectNullArguments()
    {
        var registry = new CheckpointTypeRegistry();

        Should.Throw<ArgumentException>(() => registry.Register(null!, "host:x"));
        Should.Throw<ArgumentException>(() => registry.Register<Order>(null!));
    }
}

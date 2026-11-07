namespace Voluta.Abstractions.Serialization;

/// <summary>
///     Host-owned registry of checkpoint value types. Registered types are stamped by
///     durable providers into the wire (<c>{"$type","$value"}</c>) and restored as typed
///     instances instead of raw <c>JsonElement</c>. Surfaced to providers via
///     <c>CompileOptions.CheckpointTypes</c> (the engine assigns it to
///     <see cref="Checkpoint.IRegistryAwareCheckpointer.TypeRegistry" /> when supported).
/// </summary>
/// <remarks>
///     Type ids are stable host-owned strings (e.g. <c>"host:order"</c>). Registration is
///     one-directional and collision-checked: a duplicate id with a different CLR type, or
///     a duplicate CLR type with a different id, throws <see cref="ArgumentException" />.
/// </remarks>
public sealed class CheckpointTypeRegistry
{
    private readonly Dictionary<string, Type> byTypeId = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, string> byRuntimeType = [];

    /// <summary>
    ///     Creates an empty registry ready for fluent <see cref="Register{T}" /> calls.
    /// </summary>
    public CheckpointTypeRegistry()
    {
    }

    /// <summary>Empty registry (no registered types).</summary>
    public static CheckpointTypeRegistry Empty { get; } = new();

    /// <summary>
    ///     Registers <typeparamref name="T" /> under <paramref name="typeId" /> (fluent).
    /// </summary>
    /// <typeparam name="T">Runtime value type.</typeparam>
    /// <param name="typeId">Stable wire type id (non-empty, &lt;= 128 chars).</param>
    /// <returns>This registry for chaining.</returns>
    /// <exception cref="System.ArgumentException">
    ///     Id or type already registered with a different counterpart.
    /// </exception>
    public CheckpointTypeRegistry Register<T>(string typeId)
        where T : notnull
    {
        return Register(typeof(T), typeId);
    }

    /// <summary>
    ///     Registers <paramref name="runtimeType" /> under <paramref name="typeId" /> (fluent).
    /// </summary>
    /// <param name="runtimeType">Runtime value type.</param>
    /// <param name="typeId">Stable wire type id (non-empty, &lt;= 128 chars).</param>
    /// <returns>This registry for chaining.</returns>
    /// <exception cref="System.ArgumentException">
    ///     Null type or id; empty/oversized id; id already registered with a different CLR
    ///     type; type already registered with a different id.
    /// </exception>
    public CheckpointTypeRegistry Register(Type runtimeType, string typeId)
    {
        if (runtimeType is null)
        {
            throw new ArgumentException("Checkpoint type must be non-null.", nameof(runtimeType));
        }

        if (typeId is null)
        {
            throw new ArgumentException("Checkpoint type id must be non-null.", nameof(typeId));
        }

        if (typeId.Length is 0 or > 128)
        {
            throw new ArgumentException(
                "Checkpoint type id must be a non-empty string of at most 128 characters.",
                nameof(typeId));
        }

        if (byTypeId.TryGetValue(typeId, out var existingType))
        {
            return existingType == runtimeType
                ? this
                : throw new ArgumentException(
                    $"Checkpoint type id '{typeId}' is already registered for '{existingType.FullName}'.",
                    nameof(typeId));
        }

        if (byRuntimeType.TryGetValue(runtimeType, out var existingId))
        {
            throw new ArgumentException(
                $"Type '{runtimeType.FullName}' is already registered as '{existingId}'.",
                nameof(runtimeType));
        }


        byTypeId[typeId] = runtimeType;
        byRuntimeType[runtimeType] = typeId;
        return this;
    }

    /// <summary>
    ///     Looks up the wire type id for a runtime type.
    /// </summary>
    /// <param name="runtimeType">Runtime value type.</param>
    /// <param name="typeId">Registered id when found.</param>
    /// <returns><see langword="true" /> when the type is registered.</returns>
    public bool TryGetTypeId(Type runtimeType, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? typeId)
    {
        return runtimeType is null
            ? throw new ArgumentException("Runtime type must be non-null.", nameof(runtimeType))
            : byRuntimeType.TryGetValue(runtimeType, out typeId);
    }

    /// <summary>
    ///     Resolves a wire type id back to the runtime type.
    /// </summary>
    /// <param name="typeId">Wire type id.</param>
    /// <param name="type">Registered runtime type when found.</param>
    /// <returns><see langword="true" /> when the id is registered.</returns>
    public bool TryResolveType(string typeId, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Type? type)
    {
        return typeId is null
            ? throw new ArgumentException("Type id must be non-null.", nameof(typeId))
            : byTypeId.TryGetValue(typeId, out type);
    }
}

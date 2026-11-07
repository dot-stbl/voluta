using Voluta.Abstractions.Serialization;

namespace Voluta.Abstractions.Checkpoint;

/// <summary>
///     Capability interface for durable providers that stamp registered host types into
///     the wire and restore typed values. The engine assigns <see cref="TypeRegistry" />
///     when the compiled graph carries <c>CompileOptions.CheckpointTypes</c> and the
///     provider implements this interface; <see cref="ICheckpointer" /> itself is never
///     widened. In-memory checkpoints do not implement it (no serialization — zero
///     behavior change).
/// </summary>
public interface IRegistryAwareCheckpointer
{
    /// <summary>
    ///     Host type registry used for wire stamping / typed restore.
    ///     <see langword="null" /> (default) keeps the unstamped allow-list behavior.
    /// </summary>
    public CheckpointTypeRegistry? TypeRegistry { get; set; }
}

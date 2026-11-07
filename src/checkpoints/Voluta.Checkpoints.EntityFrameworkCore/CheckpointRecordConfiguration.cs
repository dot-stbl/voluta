using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Voluta.Abstractions.Checkpoint;
using Voluta.Abstractions.Runtime;
using Voluta.Checkpoints.EntityFrameworkCore.Wire;

namespace Voluta.Checkpoints.EntityFrameworkCore;

/// <summary>
///     Fluent configuration for <see cref="CheckpointRecord" />.
///     Column names follow the host naming convention (no forced snake_case).
/// </summary>
internal sealed class CheckpointRecordConfiguration : IEntityTypeConfiguration<CheckpointRecord>
{
    /// <summary>Default table name for checkpoint rows.</summary>
    internal const string TableName = "voluta_checkpoints";

    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<CheckpointRecord> builder)
    {
        builder.ToTable(TableName);
        builder.HasKey(static record => new { record.ThreadId, record.Step });

        builder.Property(static record => record.ThreadId)
            .HasMaxLength(512)
            .IsRequired();

        builder.Property(static record => record.Step)
            .IsRequired();

        builder.Property(static record => record.Status)
            .HasMaxLength(64)
            .HasConversion(
                static status => status.ToString(),
                static value => CheckpointStatusConversion.Parse(value))
            .IsRequired();

        var snapshotConverter = new ValueConverter<CheckpointSnapshot, string>(
            static snapshot => JsonSerializer.Serialize(
                EfCheckpointDocument.FromSnapshot(snapshot),
                JsonSerializerOptions.Web),
            static json => EfCheckpointDocument.DeserializeToSnapshot(json));

        var snapshotComparer = new ValueComparer<CheckpointSnapshot>(
            static (left, right) => ReferenceEquals(left, right),
            static snapshot => snapshot.ThreadId.GetHashCode(StringComparison.Ordinal) ^ snapshot.Step.GetHashCode(),
            static snapshot => snapshot);

        builder.Property(static record => record.Snapshot)
            .HasConversion(snapshotConverter)
            .Metadata.SetValueComparer(snapshotComparer);

        builder.Property(static record => record.Snapshot)
            .IsRequired();

        // Application-managed concurrency token (same-step rewrite races →
        // DbUpdateConcurrencyException → checkpoint.conflict). Not store-generated so the
        // configuration stays portable across SQLite / InMemory / Postgres.
        builder.Property(static record => record.ConcurrencyToken)
            .IsConcurrencyToken();

        builder.HasIndex(static record => record.ThreadId);
    }
}

/// <summary>
///     Status string conversion helpers (must not use <c>out</c> in expression trees).
/// </summary>
file static class CheckpointStatusConversion
{
    public static GraphRunStatus Parse(string value)
    {
        return Enum.TryParse<GraphRunStatus>(value, ignoreCase: true, out var parsed)
            ? parsed
            : GraphRunStatus.Running;
    }
}

using ISC.AI.Modules.Media.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ISC.AI.Modules.Media.Data.EntityConfigurations;

/// <summary>Конфигурация частей незавершённой индексации видео (<c>media.index_checkpoint</c>, ТНД-007, ADR-0064).</summary>
public sealed class MediaIndexCheckpointConfiguration : IEntityTypeConfiguration<MediaIndexCheckpoint>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<MediaIndexCheckpoint> builder)
    {
        builder.ToTable("index_checkpoint", MediaDbContext.Schema);
        builder.HasKey(c => c.Id);

        builder.Property(c => c.RunKey).HasMaxLength(64).IsRequired();
        builder.Property(c => c.Faces).HasColumnType("jsonb").IsRequired();

        // Каждый кадр — ровно в одной части (ТНД-002, «кадр × область»): страховка к проверке перекрытия при записи.
        builder.HasIndex(c => new { c.AssetId, c.FirstFrameIndex }).IsUnique();

        // Каскад ВНУТРИ схемы: уничтожение носителя (ТБ-064) уносит и шаблоны незавершённой индексации.
        builder.HasOne(c => c.Asset).WithMany()
               .HasForeignKey(c => c.AssetId).OnDelete(DeleteBehavior.Cascade);
    }
}

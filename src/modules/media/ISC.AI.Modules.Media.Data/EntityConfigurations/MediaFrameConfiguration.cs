using ISC.AI.Modules.Media.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ISC.AI.Modules.Media.Data.EntityConfigurations;

/// <summary>Конфигурация таблицы кадров (<c>media.frame</c>).</summary>
public sealed class MediaFrameConfiguration : IEntityTypeConfiguration<MediaFrame>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<MediaFrame> builder)
    {
        builder.ToTable("frame", MediaDbContext.Schema);
        builder.HasKey(f => f.Id);

        builder.HasIndex(f => new { f.AssetId, f.Index }).IsUnique();

        // Каскад ВНУТРИ схемы: удаление носителя снимает кадры (ТБ-064, GATE-6).
        builder.HasOne(f => f.Asset).WithMany()
               .HasForeignKey(f => f.AssetId).OnDelete(DeleteBehavior.Cascade);
    }
}

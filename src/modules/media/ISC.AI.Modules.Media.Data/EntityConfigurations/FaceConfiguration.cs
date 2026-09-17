using ISC.AI.Modules.Media.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ISC.AI.Modules.Media.Data.EntityConfigurations;

/// <summary>Конфигурация таблицы лиц (<c>media.face</c>).</summary>
public sealed class FaceConfiguration : IEntityTypeConfiguration<Face>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Face> builder)
    {
        builder.ToTable("face", MediaDbContext.Schema);
        builder.HasKey(f => f.Id);

        // Пять точек x 2 координаты — массив real[] Postgres (Npgsql маппит float[] нативно).
        builder.Property(f => f.Landmarks).IsRequired();
        builder.Property(f => f.QualityReason).HasMaxLength(200);
        builder.Property(f => f.CropStoredFileName).HasMaxLength(64);

        builder.Property(f => f.Classification).IsRequired();
        builder.Property(f => f.DivisionId).IsRequired();

        builder.HasIndex(f => f.AssetId);
        builder.HasIndex(f => new { f.Classification, f.DivisionId });
        builder.HasIndex(f => f.CropStoredFileName).IsUnique().HasFilter("crop_stored_file_name IS NOT NULL");

        // Каскад ВНУТРИ схемы (ТБ-064, GATE-6): носитель → лица; кадр → лица.
        builder.HasOne(f => f.Asset).WithMany()
               .HasForeignKey(f => f.AssetId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(f => f.Frame).WithMany()
               .HasForeignKey(f => f.FrameId).OnDelete(DeleteBehavior.Cascade);
    }
}

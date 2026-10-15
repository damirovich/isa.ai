using ISC.AI.Modules.Media.Data.Entities;
using ISC.AI.Modules.Media.Domain.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ISC.AI.Modules.Media.Data.EntityConfigurations;

/// <summary>Конфигурация слоя пометок (<c>media.annotation</c>, ТФ-МЕД-13, ADR-0050).</summary>
public sealed class MediaAnnotationConfiguration : IEntityTypeConfiguration<MediaAnnotation>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<MediaAnnotation> builder)
    {
        builder.ToTable("annotation", MediaDbContext.Schema);
        builder.HasKey(a => a.Id);

        builder.Property(a => a.Text).HasMaxLength(AnnotationDraft.MaxTextLength);
        builder.Property(a => a.Color).HasMaxLength(16).IsRequired();

        // Режимные поля обязательны (ТБ-020): пометка без грифа в схему не попадает.
        builder.Property(a => a.Classification).IsRequired();
        builder.Property(a => a.DivisionId).IsRequired();

        builder.HasIndex(a => a.AssetId);
        builder.HasIndex(a => new { a.Classification, a.DivisionId });

        // Каскад ВНУТРИ схемы (ТНД-008, ADR-0025): уничтожение носителя снимает его пометки.
        builder.HasOne(a => a.Asset).WithMany()
               .HasForeignKey(a => a.AssetId).OnDelete(DeleteBehavior.Cascade);
    }
}

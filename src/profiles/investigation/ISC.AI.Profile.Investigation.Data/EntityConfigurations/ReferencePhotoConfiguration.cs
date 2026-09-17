using ISC.AI.Profile.Investigation.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ISC.AI.Profile.Investigation.Data.EntityConfigurations;

/// <summary>Конфигурация таблицы эталонов (<c>investigation.reference_photo</c>, ТБ-077).</summary>
public class ReferencePhotoConfiguration : IEntityTypeConfiguration<ReferencePhoto>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ReferencePhoto> builder)
    {
        builder.ToTable("reference_photo", InvestigationDbContext.Schema);
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Source).HasMaxLength(1000);
        builder.Property(e => e.LegalBasis).HasMaxLength(1000);
        builder.Property(e => e.ReviewDueAt).HasColumnType("date");

        // Гриф эталона — не ниже грифа носителя (ТБ-070), NOT NULL.
        builder.Property(e => e.Classification).IsRequired();
        builder.Property(e => e.DivisionId).IsRequired();

        // FK внутри схемы (ТО-инф-08); MediaAssetId/MediaFaceId — по значению → схема media.
        builder.HasOne(e => e.Person).WithMany()
               .HasForeignKey(e => e.PersonId).OnDelete(DeleteBehavior.Cascade);

        // Обратный поиск «какие эталоны построены на носителе» при гарантированном удалении (ТБ-074).
        builder.HasIndex(e => e.MediaAssetId);

        // SupersededById — ссылка на эталон-замену в той же таблице; FK не ставим: прежний эталон
        // сохраняется навсегда (ТБ-077), а самоссылка с каскадом только мешала бы удалению фигуранта.
    }
}

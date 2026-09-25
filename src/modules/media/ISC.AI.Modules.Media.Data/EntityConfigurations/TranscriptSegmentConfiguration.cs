using ISC.AI.Modules.Media.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ISC.AI.Modules.Media.Data.EntityConfigurations;

/// <summary>Конфигурация таблицы фрагментов расшифровки (<c>media.transcript_segment</c>, ADR-0026).</summary>
public sealed class TranscriptSegmentConfiguration : IEntityTypeConfiguration<TranscriptSegment>
{
    /// <summary>Предел длины версии модели (имя модели + пин файлов).</summary>
    public const int ModelVersionMaxLength = 200;

    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<TranscriptSegment> builder)
    {
        builder.ToTable("transcript_segment", MediaDbContext.Schema);
        builder.HasKey(s => s.Id);

        // Текст — без предела длины (text): границы фрагмента задаёт детектор речи, а не схема; обрезать
        // первичный слой нельзя.
        builder.Property(s => s.Text).IsRequired();
        builder.Property(s => s.ModelVersion).HasMaxLength(ModelVersionMaxLength).IsRequired();

        // Режимные поля обязательны (ТБ-020): фрагмент без грифа в схему не попадает.
        builder.Property(s => s.Classification).IsRequired();
        builder.Property(s => s.DivisionId).IsRequired();

        // Порядок фрагментов носителя — и ключ выборки карточки/поиска (поиск идёт по носителям дела).
        builder.HasIndex(s => new { s.AssetId, s.Index }).IsUnique();
        builder.HasIndex(s => new { s.Classification, s.DivisionId });

        // Каскад ВНУТРИ схемы (ТБ-064, ADR-0025): удаление носителя снимает его расшифровку.
        builder.HasOne(s => s.Asset).WithMany()
               .HasForeignKey(s => s.AssetId).OnDelete(DeleteBehavior.Cascade);
    }
}

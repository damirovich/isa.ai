using ISC.AI.Modules.Media.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ISC.AI.Modules.Media.Data.EntityConfigurations;

/// <summary>Конфигурация журнала сверок носителей с эталонами фигурантов (<c>media.suggestion_run</c>, ТФ-ПЕР-09).</summary>
public sealed class SuggestionRunConfiguration : IEntityTypeConfiguration<SuggestionRun>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<SuggestionRun> builder)
    {
        builder.ToTable("suggestion_run", MediaDbContext.Schema);
        builder.HasKey(r => r.Id);

        builder.Property(r => r.Trigger).IsRequired();

        // Режимные поля обязательны (ТБ-070): запись без грифа дела в схему не попадает.
        builder.Property(r => r.Classification).IsRequired();
        builder.Property(r => r.DivisionId).IsRequired();

        // Карточка носителя читает последнюю сверку по каждому делу носителя.
        builder.HasIndex(r => new { r.AssetId, r.CaseId, r.CreatedAt });
        builder.HasIndex(r => r.CaseId);
        builder.HasIndex(r => new { r.Classification, r.DivisionId });

        // Каскад ВНУТРИ схемы: гарантированное удаление носителя (ТБ-075) уносит и его сверки.
        builder.HasOne(r => r.Asset).WithMany()
               .HasForeignKey(r => r.AssetId).OnDelete(DeleteBehavior.Cascade);
    }
}

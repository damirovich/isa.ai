using ISC.AI.Modules.Media.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ISC.AI.Modules.Media.Data.EntityConfigurations;

/// <summary>Конфигурация таблицы кандидатов (<c>media.search_candidate</c>, ТФ-ПЛ-02).</summary>
public sealed class SearchCandidateConfiguration : IEntityTypeConfiguration<SearchCandidate>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<SearchCandidate> builder)
    {
        builder.ToTable("search_candidate", MediaDbContext.Schema);
        builder.HasKey(c => c.Id);

        builder.Property(c => c.CropStoredFileName).HasMaxLength(64);
        builder.Property(c => c.ModelVersion).HasMaxLength(50).IsRequired();

        // Режимные поля обязательны (ТБ-070). Решётка на чтении — по этой строке, без джойна.
        builder.Property(c => c.Classification).IsRequired();
        builder.Property(c => c.DivisionId).IsRequired();

        // Один ранг на сессию — кандидат-лист упорядочен и без дублей.
        builder.HasIndex(c => new { c.SessionId, c.Rank }).IsUnique();
        builder.HasIndex(c => c.Status);
        builder.HasIndex(c => new { c.Classification, c.DivisionId });

        // Каскад ВНУТРИ схемы: сессия → кандидаты. На face/asset FK НЕТ намеренно (история кандидатов
        // переживает гарантированное удаление носителя, ТБ-072 — см. описание сущности).
        builder.HasOne(c => c.Session).WithMany()
               .HasForeignKey(c => c.SessionId).OnDelete(DeleteBehavior.Cascade);
    }
}

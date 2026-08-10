using ISC.AI.Modules.DocFlow.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ISC.AI.Modules.DocFlow.Data.EntityConfigurations;

/// <summary>
/// Конфигурация документа (ТЗ СКИД §3.2): таблица <c>docflow.document</c>. Не путать с
/// <c>core.document</c> (индекс корпуса) — связь между ними появится на этапе 7 Э4-35
/// (<c>document_index_link</c>, слабая ссылка).
/// </summary>
public class DocumentConfiguration : IEntityTypeConfiguration<Document>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Document> builder)
    {
        builder.ToTable("document", DocFlowDbContext.Schema, t =>
        {
            // Страховки БД: значения перечислений только из известных наборов (как в СКИД).
            t.HasCheckConstraint("chk_document_aggregated_status", "aggregated_status IN (0, 1, 2, 3, 4, 5, 6)");
            t.HasCheckConstraint("chk_document_direction_flag", "direction_flag IN (1, 2, 3)");
            t.HasCheckConstraint("chk_document_priority", "priority IS NULL OR priority IN (1, 2, 3)");
        });

        // Оптимистическая блокировка — системная колонка PostgreSQL xmin (в миграции не создаётся).
        builder.Property(d => d.Xmin).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();

        builder.Property(d => d.RegNumber).HasMaxLength(200);
        builder.Property(d => d.Source).HasMaxLength(500);
        builder.Property(d => d.ShortContent).HasMaxLength(2000).IsRequired();
        builder.Property(d => d.Notes).HasMaxLength(2000);

        // Рег. номер уникален (ТЗ §3.2, «вручную»); NULL-ы допускаются до присвоения номера.
        builder.HasIndex(d => d.RegNumber).IsUnique();

        // Индексы дашборда/фильтров — как в СКИД (§7.2: просроченные, статистика по статусам).
        builder.HasIndex(d => new { d.AggregatedStatus, d.UpdatedAt });
        builder.HasIndex(d => d.RegDate);

        // Решётка доступа (ADR-0017 п.5): выборки «документы моего подразделения не выше допуска».
        builder.HasIndex(d => new { d.DivisionId, d.Classification });

        // FK внутри схемы docflow: тип запрещено удалять при наличии документов (опора §3.1).
        builder.HasOne(d => d.Type).WithMany().HasForeignKey(d => d.TypeId).OnDelete(DeleteBehavior.Restrict);

        // InspectorUserId/RegisteredByUserId/DivisionId — СЛАБЫЕ ссылки на core.app_user и словарь
        // подразделений решётки: FK через границу схем не создаются (ТО-инф-06).
    }
}

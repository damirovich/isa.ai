using ISC.AI.Modules.DocFlow.Domain.Entities;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ISC.AI.Modules.DocFlow.Data.EntityConfigurations;

/// <summary>
/// Конфигурация справочника типов документов (ТЗ СКИД §3.1): таблица <c>docflow.document_type</c>.
/// </summary>
public class DocumentTypeConfiguration : IEntityTypeConfiguration<DocumentType>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<DocumentType> builder)
    {
        // Страховка БД: группа — только из известного набора (Хранение=1 / Исполнение=2, §1.4).
        builder.ToTable("document_type", DocFlowDbContext.Schema,
            t => t.HasCheckConstraint("chk_document_type_group", "\"group\" IN (1, 2)"));

        builder.Property(t => t.Name).HasMaxLength(200).IsRequired();

        // Уникальность имени в справочнике — вторая линия за проверкой сценария (гонка двух операторов).
        builder.HasIndex(t => t.Name).IsUnique();
    }
}

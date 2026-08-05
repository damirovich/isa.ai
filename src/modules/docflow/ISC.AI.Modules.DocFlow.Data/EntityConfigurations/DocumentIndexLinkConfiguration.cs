using ISC.AI.Modules.DocFlow.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ISC.AI.Modules.DocFlow.Data.EntityConfigurations;

/// <summary>
/// Конфигурация мостика «документ ↔ корпус» (этап 7 Э4-35): таблица <c>docflow.document_index_link</c>.
/// FK — только на документ ВНУТРИ схемы; на <c>core.document</c> — слабая ссылка без FK (ТО-инф-06).
/// </summary>
public class DocumentIndexLinkConfiguration : IEntityTypeConfiguration<DocumentIndexLink>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<DocumentIndexLink> builder)
    {
        builder.ToTable("document_index_link", DocFlowDbContext.Schema);

        // Один актуальный корпусный документ на документ документооборота.
        builder.HasIndex(l => l.DocumentId).IsUnique();

        // Обратный поиск «чей это фрагмент» при формировании ссылки ответа чата.
        builder.HasIndex(l => l.CoreDocumentId);

        builder.HasOne<Document>().WithMany()
            .HasForeignKey(l => l.DocumentId).OnDelete(DeleteBehavior.Cascade);
    }
}

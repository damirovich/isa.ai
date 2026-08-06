using ISC.AI.Modules.DocFlow.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ISC.AI.Modules.DocFlow.Data.EntityConfigurations;

/// <summary>
/// Конфигурация назначения (ТЗ СКИД §4.1/4.2): таблица <c>docflow.document_assignment</c>.
/// </summary>
public class DocumentAssignmentConfiguration : IEntityTypeConfiguration<DocumentAssignment>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<DocumentAssignment> builder)
    {
        builder.ToTable("document_assignment", DocFlowDbContext.Schema,
            t => t.HasCheckConstraint("chk_assignment_status", "status IN (1, 2, 3, 4, 5, 6, 7)"));

        // Оптимистическая блокировка — системная колонка PostgreSQL xmin (как в СКИД).
        builder.Property(a => a.Xmin).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();

        // Удаление документа удаляет его назначения (как в СКИД).
        builder.HasOne(a => a.Document).WithMany(d => d.Assignments)
            .HasForeignKey(a => a.DocumentId).OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(a => a.DocumentId);
        builder.HasIndex(a => a.AssigneeUserId);

        // Выборки «назначения подразделения» (отчёты §6, дашборд §7.2).
        builder.HasIndex(a => a.DivisionId);

        // §4.1: одно подразделение — ОДНО назначение по документу. В СКИД этот запрет жил ТОЛЬКО в
        // валидаторе формы: уникального индекса не было, и два одновременных запроса «добавить
        // назначение» создавали два назначения на одно подразделение (подтверждено разбором
        // исходника, этап 3.2). Проверка в коде остаётся ради внятного сообщения, индекс — ради гонки.
        builder.HasIndex(a => new { a.DocumentId, a.DivisionId }).IsUnique();

        // AssigneeUserId/ControllerUserId/DivisionId — слабые ссылки (core.app_user / словарь решётки),
        // без FK через границу схем (ТО-инф-06).
    }
}

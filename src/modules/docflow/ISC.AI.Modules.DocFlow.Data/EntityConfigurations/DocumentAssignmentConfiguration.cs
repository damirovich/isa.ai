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

        // AssigneeUserId/ControllerUserId/DivisionId — слабые ссылки (core.app_user / словарь решётки),
        // без FK через границу схем (ТО-инф-06).
    }
}

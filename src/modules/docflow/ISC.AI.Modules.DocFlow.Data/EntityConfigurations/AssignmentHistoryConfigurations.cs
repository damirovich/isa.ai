using ISC.AI.Modules.DocFlow.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ISC.AI.Modules.DocFlow.Data.EntityConfigurations;

/// <summary>
/// Конфигурация перехода статуса (ТЗ СКИД §4.8): таблица <c>docflow.assignment_status_history</c>.
/// </summary>
public class AssignmentStatusHistoryConfiguration : IEntityTypeConfiguration<AssignmentStatusHistory>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<AssignmentStatusHistory> builder)
    {
        builder.ToTable("assignment_status_history", DocFlowDbContext.Schema);

        builder.Property(h => h.Comment).HasMaxLength(2000);

        // Удаление назначения (вместе с документом) удаляет его историю.
        builder.HasOne(h => h.Assignment).WithMany()
            .HasForeignKey(h => h.AssignmentId).OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(h => h.AssignmentId);
    }
}

/// <summary>
/// Конфигурация смены исполнителя (ТЗ СКИД §4.7): таблица <c>docflow.assignment_reassignment</c>.
/// </summary>
public class AssignmentReassignmentConfiguration : IEntityTypeConfiguration<AssignmentReassignment>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<AssignmentReassignment> builder)
    {
        builder.ToTable("assignment_reassignment", DocFlowDbContext.Schema);

        // Основание НЕобязательно — в отличие от продления срока (§4.6), где оно обязательно.
        builder.Property(r => r.Reason).HasMaxLength(2000);

        builder.HasOne(r => r.Assignment).WithMany()
            .HasForeignKey(r => r.AssignmentId).OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(r => r.AssignmentId);
    }
}

/// <summary>
/// Конфигурация продления срока (ТЗ СКИД §4.6): таблица <c>docflow.deadline_extension</c>.
/// </summary>
public class DeadlineExtensionConfiguration : IEntityTypeConfiguration<DeadlineExtension>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<DeadlineExtension> builder)
    {
        builder.ToTable("deadline_extension", DocFlowDbContext.Schema);

        // Основание обязательно (§4.6).
        builder.Property(e => e.Reason).HasMaxLength(2000).IsRequired();

        builder.HasOne<DocumentAssignment>().WithMany()
            .HasForeignKey(e => e.AssignmentId).OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => e.AssignmentId);
    }
}

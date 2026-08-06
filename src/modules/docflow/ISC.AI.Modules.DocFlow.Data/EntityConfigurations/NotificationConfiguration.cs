using ISC.AI.Modules.DocFlow.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ISC.AI.Modules.DocFlow.Data.EntityConfigurations;

/// <summary>Конфигурация уведомлений (<c>docflow.notification</c>, разд. 5 ТЗ СКИД).</summary>
public class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable("notification", DocFlowDbContext.Schema);

        builder.Property(n => n.MessageKey).HasMaxLength(200).IsRequired();
        builder.Property(n => n.ArgumentsJson).IsRequired();

        // Ссылки внутри схемы — настоящие FK. Каскад НЕ ставится: удаление документа не должно
        // молча стирать уже доставленные уведомления, поэтому связи обнуляются (SetNull).
        builder.HasOne<Document>().WithMany()
            .HasForeignKey(n => n.DocumentId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<DocumentAssignment>().WithMany()
            .HasForeignKey(n => n.AssignmentId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<DocumentComment>().WithMany()
            .HasForeignKey(n => n.CommentId).OnDelete(DeleteBehavior.SetNull);

        // Основной запрос — «мои непрочитанные, новые первыми».
        builder.HasIndex(n => new { n.RecipientUserId, n.IsRead, n.CreatedAt });

        // Опора дедупликации уведомлений о сроках (см. INotificationStore.RaiseAsync).
        builder.HasIndex(n => new { n.AssignmentId, n.Type, n.AboutDeadline });
    }
}

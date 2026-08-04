namespace ISC.AI.Persistence.EntityConfigurations.ConversationConfigurations;

/// <summary>Конфигурация таблицы диалогов (<c>core.conversation</c>).</summary>
public class ConversationEntityConfiguration : IEntityTypeConfiguration<ConversationEntity>
{
    public void Configure(EntityTypeBuilder<ConversationEntity> builder)
    {
        builder.ToTable("conversation", CoreDbContext.Schema);

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Title).HasMaxLength(500).IsRequired();
        builder.Property(e => e.SubjectId).IsRequired();
        builder.Property(e => e.Classification).IsRequired();

        // Разграничение по владельцу: список диалогов субъекта (query-filter !IsDeleted добавляется глобально).
        builder.HasIndex(e => e.SubjectId);

        // Сообщения каскадно удаляются вместе с диалогом (мягкое удаление диалога сообщения не трогает —
        // они скрываются вместе с ним по навигации).
        builder.HasMany(e => e.Messages)
               .WithOne(m => m.Conversation)
               .HasForeignKey(m => m.ConversationId)
               .OnDelete(DeleteBehavior.Cascade);
    }
}

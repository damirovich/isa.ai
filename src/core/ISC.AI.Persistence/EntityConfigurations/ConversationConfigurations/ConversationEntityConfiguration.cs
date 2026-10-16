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

        // Область профиля (ADR-0052): короткий ключ вида «case:45»; null — область по умолчанию.
        builder.Property(e => e.Area).HasMaxLength(100);

        // Ключи уничтожения (ADR-0052) — массив text[]; пустой по умолчанию, в том числе у прежних диалогов.
        builder.Property(e => e.ScopeKeys).HasDefaultValueSql("'{}'::text[]").IsRequired();

        // Разграничение по владельцу: список диалогов субъекта (query-filter !IsDeleted добавляется глобально).
        builder.HasIndex(e => e.SubjectId);

        // Список диалогов субъекта в области — основной запрос боковой панели чата.
        builder.HasIndex(e => new { e.SubjectId, e.Area });

        // Сообщения каскадно удаляются вместе с диалогом (мягкое удаление диалога сообщения не трогает —
        // они скрываются вместе с ним по навигации).
        builder.HasMany(e => e.Messages)
               .WithOne(m => m.Conversation)
               .HasForeignKey(m => m.ConversationId)
               .OnDelete(DeleteBehavior.Cascade);
    }
}

namespace ISC.AI.Persistence.EntityConfigurations.ConversationConfigurations;

/// <summary>Конфигурация таблицы сообщений диалога (<c>core.conversation_message</c>).</summary>
public class ConversationMessageEntityConfiguration : IEntityTypeConfiguration<ConversationMessageEntity>
{
    public void Configure(EntityTypeBuilder<ConversationMessageEntity> builder)
    {
        builder.ToTable("conversation_message", CoreDbContext.Schema);

        builder.HasKey(e => e.Id);

        builder.Property(e => e.ConversationId).IsRequired();
        builder.Property(e => e.Role).IsRequired();
        builder.Property(e => e.Content).IsRequired(); // text без ограничения длины
        builder.Property(e => e.Classification).IsRequired();

        // Итог грунтовки ответа — jsonb (список ссылок и их статусы); null для реплики пользователя.
        builder.Property(e => e.GroundingJson).HasColumnType("jsonb");

        // Загрузка истории диалога по возрастанию времени.
        builder.HasIndex(e => e.ConversationId);

        // Согласованный query-filter с мягко удаляемым принципалом (диалогом): сообщения удалённого диалога
        // скрыты ВЕЗДЕ — устраняет предупреждение EF о required-связи с фильтруемым принципалом и делает
        // «удалил диалог → его сообщения не видны» явным инвариантом модели.
        builder.HasQueryFilter(m => !m.Conversation!.IsDeleted);
    }
}

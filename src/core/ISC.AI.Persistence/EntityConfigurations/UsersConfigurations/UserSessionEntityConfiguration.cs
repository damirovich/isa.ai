namespace ISC.AI.Persistence.EntityConfigurations.UsersConfigurations;

/// <summary>Конфигурация таблицы сеансов входа (<c>core.user_session</c>, ADR-0060).</summary>
public class UserSessionEntityConfiguration : IEntityTypeConfiguration<UserSessionEntity>
{
    public void Configure(EntityTypeBuilder<UserSessionEntity> builder)
    {
        builder.ToTable("user_session", CoreDbContext.Schema);

        // Номер сеанса порождает приложение (случайный Guid) — база значение не генерирует.
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();

        builder.Property(e => e.ClientAddress).HasMaxLength(UserSessionEntity.MaxClientAddressLength);
        builder.Property(e => e.UserAgent).HasMaxLength(UserSessionEntity.MaxUserAgentLength);
        builder.Property(e => e.EndComment).HasMaxLength(UserSessionEntity.MaxEndCommentLength);
        builder.Property(e => e.EndReason).HasConversion<short?>();

        // Внешний ключ БЕЗ навигации: у учёток глобальный фильтр мягкого удаления, и обязательная навигация молча
        // скрыла бы сеансы удалённой учётки (EF предупреждает об этом же). Список соединяет явно. Каскада нет —
        // учётки удаляются мягко, история сеансов остаётся.
        builder.HasOne<AppUserEntity>().WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Restrict);

        // Список «Активные сеансы» берёт только открытые — частичный индекс по последней активности.
        builder.HasIndex(e => e.LastActivityAt).HasFilter("ended_at IS NULL");
        builder.HasIndex(e => e.UserId);
    }
}

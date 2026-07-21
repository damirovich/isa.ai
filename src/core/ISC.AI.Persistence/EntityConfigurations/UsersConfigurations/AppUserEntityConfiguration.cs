namespace ISC.AI.Persistence.EntityConfigurations.UsersConfigurations;

/// <summary>Конфигурация таблицы пользователей (<c>core.app_user</c>).</summary>
public class AppUserEntityConfiguration : IEntityTypeConfiguration<AppUserEntity>
{
    public void Configure(EntityTypeBuilder<AppUserEntity> builder)
    {
        builder.ToTable("app_user", CoreDbContext.Schema);

        builder.HasKey(e => e.Id);

        builder.Property(e => e.UserName).HasMaxLength(100).IsRequired();
        builder.Property(e => e.DisplayName).HasMaxLength(200);
        builder.Property(e => e.ExternalId).HasMaxLength(100);

        // Частичный индекс: уникальность имени входа — только среди ЖИВЫХ учёток (is_deleted = false).
        // Без фильтра мягко удалённая учётка занимает имя навечно — JIT-создание при переиспользовании
        // логина во внешней системе падает на уникальном индексе вместо единого отказа (ТД-003).
        builder.HasIndex(e => e.UserName).IsUnique().HasFilter("is_deleted = false");

        // Привязка к внешней учётке уникальна (одна внешняя учётка — один локальный субъект);
        // частичный индекс: локальные учётки без привязки (NULL) не конфликтуют.
        builder.HasIndex(e => e.ExternalId).IsUnique().HasFilter("external_id IS NOT NULL");

        builder.HasOne(e => e.Clearance).WithOne(c => c.User)
               .HasForeignKey<ClearanceEntity>(c => c.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

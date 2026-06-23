namespace ISC.AI.Persistence.EntityConfigurations.UsersConfigurations;

/// <summary>Конфигурация таблицы пользователей (<c>core.app_user</c>).</summary>
public class AppUserEntityConfiguration : IEntityTypeConfiguration<AppUserEntity>
{
    public void Configure(EntityTypeBuilder<AppUserEntity> builder)
    {
        builder.ToTable("appUser", CoreDbContext.Schema);

        builder.HasKey(e => e.Id);

        builder.Property(e => e.UserName).HasMaxLength(100).IsRequired();
        builder.Property(e => e.DisplayName).HasMaxLength(200);

        builder.HasIndex(e => e.UserName).IsUnique().HasDatabaseName("ixAppUserUsername");

        builder.HasOne(e => e.Clearance).WithOne(c => c.User)
               .HasForeignKey<ClearanceEntity>(c => c.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

namespace ISC.AI.Persistence.EntityConfigurations.UsersConfigurations;

/// <summary>Конфигурация таблицы допусков (<c>core.clearance</c>).</summary>
public class ClearanceEntityConfiguration : IEntityTypeConfiguration<ClearanceEntity>
{
    public void Configure(EntityTypeBuilder<ClearanceEntity> builder)
    {
        builder.ToTable("clearance", CoreDbContext.Schema);

        builder.HasKey(e => e.Id);

        builder.Property(e => e.MaxClassification).IsRequired();

        builder.HasIndex(e => e.UserId).IsUnique();
    }
}

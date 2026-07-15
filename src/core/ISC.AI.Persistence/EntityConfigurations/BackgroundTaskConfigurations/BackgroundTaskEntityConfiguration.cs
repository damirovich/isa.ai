namespace ISC.AI.Persistence.EntityConfigurations.BackgroundTaskConfigurations;

/// <summary>Конфигурация таблицы фоновых задач (<c>core.background_task</c>, Э4-20).</summary>
public class BackgroundTaskEntityConfiguration : IEntityTypeConfiguration<BackgroundTaskEntity>
{
    public void Configure(EntityTypeBuilder<BackgroundTaskEntity> builder)
    {
        builder.ToTable("background_task", CoreDbContext.Schema);

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever(); // Guid задаётся при постановке, не БД

        builder.Property(e => e.Kind).HasMaxLength(200).IsRequired();
        builder.Property(e => e.Status).IsRequired();
        builder.Property(e => e.Error).HasMaxLength(8000);

        // Индекс под восстановление осиротевших при старте (выборка по незавершённым статусам).
        builder.HasIndex(e => e.Status);
    }
}

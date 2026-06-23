namespace ISC.AI.Persistence.EntityConfigurations.IndexingConfigurations;

/// <summary>Конфигурация таблицы заданий индексации (<c>core.indexing_job</c>).</summary>
public class IndexingJobEntityConfiguration : IEntityTypeConfiguration<IndexingJobEntity>
{
    public void Configure(EntityTypeBuilder<IndexingJobEntity> builder)
    {
        builder.ToTable("indexingJob", CoreDbContext.Schema);

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Status).IsRequired();
        builder.Property(e => e.Error).HasMaxLength(8000);

        builder.HasIndex(e => e.Status).HasDatabaseName("ixIndexingJobStatus");
    }
}

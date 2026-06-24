namespace ISC.AI.Persistence.EntityConfigurations.CorpusConfigurations;

/// <summary>Конфигурация таблицы фрагментов (<c>core.chunk</c>).</summary>
public class ChunkEntityConfiguration : IEntityTypeConfiguration<ChunkEntity>
{
    public void Configure(EntityTypeBuilder<ChunkEntity> builder)
    {
        builder.ToTable("chunk", CoreDbContext.Schema);

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Text).IsRequired();

        // Денормализованные режимные метаданные на чанк — для фильтра доступа на стороне БД (ТБ-020). NOT NULL.
        builder.Property(e => e.Classification).IsRequired();
        builder.Property(e => e.DivisionId).IsRequired();

        builder.HasIndex(e => new { e.Classification, e.DivisionId });
        builder.HasIndex(e => new { e.DocumentId, e.Ordinal });

        builder.HasOne(e => e.Document).WithMany(d => d.Chunks)
               .HasForeignKey(e => e.DocumentId).OnDelete(DeleteBehavior.Cascade);

        // Нейтральный флаг годности источника (ADR-0013): по умолчанию актуален.
        builder.Property(e => e.IsCurrent).IsRequired().HasDefaultValue(true);
    }
}

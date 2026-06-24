namespace ISC.AI.Persistence.EntityConfigurations.CorpusConfigurations;

/// <summary>Конфигурация таблицы эмбеддингов (<c>core.embedding</c>) с вектором pgvector (ТО-инф-02).</summary>
public class EmbeddingEntityConfiguration : IEntityTypeConfiguration<EmbeddingEntity>
{
    public void Configure(EntityTypeBuilder<EmbeddingEntity> builder)
    {
        builder.ToTable("embedding", CoreDbContext.Schema);

        builder.HasKey(e => e.Id);

        // Тип столбца vector(N) фиксирует размерность эмбеддера (ADR-0011).
        builder.Property(e => e.Embedding)
               .HasColumnType($"vector({EmbeddingEntity.Dimensions})")
               .IsRequired();

        builder.Property(e => e.ModelKey).HasMaxLength(100).IsRequired();

        // Режим + флаг годности денормализованы на вектор (ТБ-020/024, ADR-0013). NOT NULL.
        builder.Property(e => e.Classification).IsRequired();
        builder.Property(e => e.DivisionId).IsRequired();
        builder.Property(e => e.IsCurrent).IsRequired().HasDefaultValue(true);

        // Pre-filter доступа на стороне БД (ТБ-020): B-tree по режимным полям рядом с ANN-поиском.
        builder.HasIndex(e => new { e.Classification, e.DivisionId });

        // ANN-индекс семантического поиска: HNSW + косинусная метрика. Параметры/метрика —
        // тюнинг ADR-0007 (ТБ-022) и выбор эмбеддера (ТО-прог-03); здесь — рабочий дефолт.
        builder.HasIndex(e => e.Embedding)
               .HasMethod("hnsw")
               .HasOperators("vector_cosine_ops");

        builder.HasOne(e => e.Chunk).WithMany()
               .HasForeignKey(e => e.ChunkId).OnDelete(DeleteBehavior.Cascade);
    }
}

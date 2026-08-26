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

        // ANN-индекс семантического поиска: HNSW + косинусная метрика (ADR-0007, ТБ-022).
        // ef_construction=512 (дефолт pgvector — 64) ОБЯЗАТЕЛЕН: при дефолтной сборке граф теряет
        // связность с малыми семантическими «островами» — документ есть в корпусе, точный скан его
        // находит, индексный нет (инцидент 26.08.2026, воспроизведён на копии данных; лечится ТОЛЬКО
        // парой ef_construction=512 при сборке + hnsw.ef_search=200 при поиске — второе выставляет
        // PgVectorRetriever). m=16 — дефолт, зафиксирован явно. Сборка на большом корпусе требует
        // maintenance_work_mem по размеру графа (см. docs/reference/pgvector-hnsw-recall.md).
        builder.HasIndex(e => e.Embedding)
               .HasMethod("hnsw")
               .HasOperators("vector_cosine_ops")
               .HasStorageParameter("m", 16)
               .HasStorageParameter("ef_construction", 512);

        builder.HasOne(e => e.Chunk).WithMany()
               .HasForeignKey(e => e.ChunkId).OnDelete(DeleteBehavior.Cascade);
    }
}

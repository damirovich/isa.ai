namespace ISC.AI.Profile.Inspector.Data.EntityConfigurations;

/// <summary>
/// Конфигурация связки «редакция ↔ чанк ядра» (<c>inspector.chunkRevisionLink</c>).
/// <c>ChunkId</c> — слабая ссылка по значению на <c>core.chunk</c> БЕЗ FK через границу схем (ТО-инф-06).
/// </summary>
public class ChunkRevisionLinkConfiguration : IEntityTypeConfiguration<ChunkRevisionLink>
{
    public void Configure(EntityTypeBuilder<ChunkRevisionLink> builder)
    {
        builder.ToTable("chunk_revision_link", InspectorDbContext.Schema);

        builder.HasKey(e => e.Id);

        // FK на редакцию — внутри схемы inspector.
        builder.HasOne(e => e.Revision).WithMany()
               .HasForeignKey(e => e.NormRevisionId).OnDelete(DeleteBehavior.Cascade);

        // Слабая ссылка на core.chunk.Id (по значению, без FK). Индекс — для материализации is_current и очистки (ТБ-064).
        builder.Property(e => e.ChunkId).IsRequired();
        builder.HasIndex(e => e.ChunkId);

        // УНИКАЛЬНО: чанк у редакции — одна связка (иначе гонка привязки задваивает материализацию).
        builder.HasIndex(e => new { e.NormRevisionId, e.ChunkId }).IsUnique();
    }
}

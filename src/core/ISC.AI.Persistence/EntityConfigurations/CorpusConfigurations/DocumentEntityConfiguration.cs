namespace ISC.AI.Persistence.EntityConfigurations.CorpusConfigurations;

/// <summary>Конфигурация таблицы документов (<c>core.document</c>).</summary>
public class DocumentEntityConfiguration : IEntityTypeConfiguration<DocumentEntity>
{
    public void Configure(EntityTypeBuilder<DocumentEntity> builder)
    {
        builder.ToTable("document", CoreDbContext.Schema);

        builder.HasKey(e => e.Id);

        builder.Property(e => e.DocType).HasMaxLength(100).IsRequired();
        builder.Property(e => e.Title).HasMaxLength(1000).IsRequired();
        builder.Property(e => e.Source).HasMaxLength(700);

        // Режимные метаданные NOT NULL — опора fail-closed фильтра доступа (ТБ-024/021).
        builder.Property(e => e.Classification).IsRequired();
        builder.Property(e => e.DivisionId).IsRequired();

        // Индекс под фильтр доступа на извлечении (ТБ-020).
        builder.HasIndex(e => new { e.Classification, e.DivisionId })
               .HasDatabaseName("ixDocumentClassificationDivision");
        builder.HasIndex(e => e.ContentHash).HasDatabaseName("ixDocumentContentHash"); // дедупликация (ТНД-002)
    }
}

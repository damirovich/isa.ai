namespace ISC.AI.Profile.Inspector.Data.EntityConfigurations;

/// <summary>
/// Конфигурация связки «норма ↔ документ ядра» (<c>inspector.normDocumentLink</c>).
/// <c>DocumentId</c> — слабая ссылка по значению на <c>core.document</c> БЕЗ FK через границу схем (ТО-инф-06).
/// </summary>
public class NormDocumentLinkConfiguration : IEntityTypeConfiguration<NormDocumentLink>
{
    public void Configure(EntityTypeBuilder<NormDocumentLink> builder)
    {
        builder.ToTable("norm_document_link", InspectorDbContext.Schema);

        builder.HasKey(e => e.Id);

        // FK на норму — внутри схемы inspector.
        builder.HasOne(e => e.LegalNorm).WithMany()
               .HasForeignKey(e => e.LegalNormId).OnDelete(DeleteBehavior.Cascade);

        // Слабая ссылка на core.document.Id (по значению, без FK). Индекс — для очистки осиротевших связок (ТБ-064).
        builder.Property(e => e.DocumentId).IsRequired();
        builder.HasIndex(e => e.DocumentId);

        // УНИКАЛЬНО: один документ у нормы — одна связка. Предварительная проверка в хранилище
        // не переживает гонку (двойной клик, два оператора), а дубль связки нечем чинить —
        // операции отвязки в картотеке нет (ревью 2026-08-10).
        builder.HasIndex(e => new { e.LegalNormId, e.DocumentId }).IsUnique();
    }
}

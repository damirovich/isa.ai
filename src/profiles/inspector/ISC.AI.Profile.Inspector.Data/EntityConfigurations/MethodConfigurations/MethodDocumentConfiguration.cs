namespace ISC.AI.Profile.Inspector.Data.EntityConfigurations.MethodConfigurations;

/// <summary>Конфигурация реестра методик (<c>inspector.method_document</c>, §5.2.9 / Ц-03).</summary>
public class MethodDocumentConfiguration : IEntityTypeConfiguration<MethodDocument>
{
    public void Configure(EntityTypeBuilder<MethodDocument> builder)
    {
        builder.ToTable("method_document", InspectorDbContext.Schema);
        builder.HasKey(e => e.Id);

        builder.Property(e => e.ArtifactKind).IsRequired().HasMaxLength(100);
        builder.Property(e => e.InspectionType).IsRequired().HasMaxLength(200);
        builder.Property(e => e.Scope).IsRequired().HasMaxLength(300);
        // Текст и метки грунтовки — без предела: длину черновика задаёт лимит генерации, не схема.
        builder.Property(e => e.Body).IsRequired();
        builder.Property(e => e.Status).IsRequired();
        builder.Property(e => e.Classification).IsRequired();

        // Индексы под реестр: список фильтруется по грифу (допуск, ТБ-020-стиль), виду и статусу.
        builder.HasIndex(e => new { e.Classification, e.CreatedAt });
        builder.HasIndex(e => e.ArtifactKind);
        builder.HasIndex(e => e.Status);
    }
}

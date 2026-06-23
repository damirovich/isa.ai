namespace ISC.AI.Profile.Inspector.Data.EntityConfigurations;

/// <summary>Конфигурация таблицы норм НПА (<c>inspector.legalNorm</c>).</summary>
public class LegalNormConfiguration : IEntityTypeConfiguration<LegalNorm>
{
    public void Configure(EntityTypeBuilder<LegalNorm> builder)
    {
        builder.ToTable("legalNorm", InspectorDbContext.Schema);

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Identifier).HasMaxLength(200).IsRequired();
        builder.Property(e => e.Title).HasMaxLength(1000).IsRequired();

        builder.HasIndex(e => e.Identifier).HasDatabaseName("ixLegalNormIdentifier");
    }
}

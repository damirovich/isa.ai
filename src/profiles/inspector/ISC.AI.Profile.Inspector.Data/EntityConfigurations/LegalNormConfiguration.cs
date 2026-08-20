namespace ISC.AI.Profile.Inspector.Data.EntityConfigurations;

/// <summary>Конфигурация таблицы норм НПА (<c>inspector.legalNorm</c>).</summary>
public class LegalNormConfiguration : IEntityTypeConfiguration<LegalNorm>
{
    public void Configure(EntityTypeBuilder<LegalNorm> builder)
    {
        builder.ToTable("legal_norm", InspectorDbContext.Schema);

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Identifier).HasMaxLength(200).IsRequired();
        builder.Property(e => e.Title).HasMaxLength(1000).IsRequired();

        // УНИКАЛЬНЫЙ: номер НПА — ключ, на который ссылается грунтовка; две нормы с одним номером
        // сделали бы ссылку неоднозначной. До картотеки (2026-08-10) индекс был неуникальным —
        // таблицу никто не наполнял, и дублей возникнуть не могло.
        builder.HasIndex(e => e.Identifier).IsUnique();
    }
}

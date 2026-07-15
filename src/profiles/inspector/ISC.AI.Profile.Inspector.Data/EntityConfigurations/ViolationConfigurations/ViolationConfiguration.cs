namespace ISC.AI.Profile.Inspector.Data.EntityConfigurations.ViolationConfigurations;

/// <summary>Конфигурация таблицы нарушений (<c>inspector.violation</c>) — ключевой объект аналитики (§5.3.2.8).</summary>
public class ViolationConfiguration : IEntityTypeConfiguration<Violation>
{
    public void Configure(EntityTypeBuilder<Violation> builder)
    {
        builder.ToTable("violation", InspectorDbContext.Schema);
        builder.HasKey(e => e.Id);

        // Обязательные поля (критерий приёмки Э5-01, §5.3.2.8): подразделение, вид, тяжесть.
        builder.Property(e => e.Severity).IsRequired();
        builder.Property(e => e.RemediationStatus).IsRequired();
        builder.Property(e => e.DetectedAt).IsRequired();

        // Ссылки на СКИД — по значению (строкой), без FK через границу схем (ТО-инф-06).
        builder.Property(e => e.SourceDocRef).HasMaxLength(200);
        builder.Property(e => e.SourceAssignmentRef).HasMaxLength(200);
        builder.Property(e => e.ReferenceDocRef).HasMaxLength(200);
        builder.Property(e => e.Cause).HasMaxLength(4000);
        builder.Property(e => e.Recommendation).HasMaxLength(4000);

        // FK внутри схемы inspector. Restrict — не удалять подразделение/вид, если по ним есть история.
        builder.HasOne(e => e.Division).WithMany()
               .HasForeignKey(e => e.DivisionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(e => e.Category).WithMany()
               .HasForeignKey(e => e.CategoryId).OnDelete(DeleteBehavior.Restrict);

        // Индексы под аналитику риска/мониторинга: по подразделению+дате, виду, статусу устранения.
        builder.HasIndex(e => new { e.DivisionId, e.DetectedAt });
        builder.HasIndex(e => e.CategoryId);
        builder.HasIndex(e => e.RemediationStatus);
    }
}

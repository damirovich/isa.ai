namespace ISC.AI.Profile.Inspector.Data.EntityConfigurations;

/// <summary>Конфигурация таблицы редакций НПА (<c>inspector.normRevision</c>).</summary>
public class NormRevisionConfiguration : IEntityTypeConfiguration<NormRevision>
{
    public void Configure(EntityTypeBuilder<NormRevision> builder)
    {
        builder.ToTable("norm_revision", InspectorDbContext.Schema);

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Status).IsRequired();

        // FK внутри схемы inspector (границу схем не пересекает, ТО-инф-06).
        builder.HasOne(e => e.Norm).WithMany(n => n.Revisions)
               .HasForeignKey(e => e.NormId).OnDelete(DeleteBehavior.Cascade);

        // Быстрый выбор действующей редакции по умолчанию (ТО-инф-04, GATE-3).
        builder.HasIndex(e => new { e.NormId, e.Status });

        // Сопоставление с редакцией внешнего источника (автонаполнение картотеки): у нормы
        // не может быть двух редакций с одним внешним ключом (частичная уникальность — ручные
        // редакции без ключа не ограничиваются).
        builder.Property(e => e.ExternalEditionId).HasMaxLength(100);
        builder.HasIndex(e => new { e.NormId, e.ExternalEditionId }).IsUnique()
               .HasFilter("external_edition_id IS NOT NULL");
    }
}

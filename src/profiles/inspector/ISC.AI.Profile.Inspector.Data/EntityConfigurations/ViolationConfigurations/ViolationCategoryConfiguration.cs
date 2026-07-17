namespace ISC.AI.Profile.Inspector.Data.EntityConfigurations.ViolationConfigurations;

/// <summary>Конфигурация 2-уровневого классификатора видов нарушений (<c>inspector.violation_category</c>).</summary>
public class ViolationCategoryConfiguration : IEntityTypeConfiguration<ViolationCategory>
{
    public void Configure(EntityTypeBuilder<ViolationCategory> builder)
    {
        builder.ToTable("violation_category", InspectorDbContext.Schema);
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Name).HasMaxLength(500).IsRequired();

        // Самоссылка «сфера → вид» (внутри схемы inspector). Restrict — сферу с видами каскадно не удалить.
        builder.HasOne(e => e.Parent).WithMany(e => e.Children)
               .HasForeignKey(e => e.ParentId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(e => e.ParentId);
    }
}

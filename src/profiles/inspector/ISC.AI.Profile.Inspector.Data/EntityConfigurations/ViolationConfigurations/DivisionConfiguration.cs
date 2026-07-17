namespace ISC.AI.Profile.Inspector.Data.EntityConfigurations.ViolationConfigurations;

/// <summary>Конфигурация таблицы подразделений (<c>inspector.division</c>, иерархия ТУ→РО).</summary>
public class DivisionConfiguration : IEntityTypeConfiguration<Division>
{
    public void Configure(EntityTypeBuilder<Division> builder)
    {
        builder.ToTable("division", InspectorDbContext.Schema);
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Name).HasMaxLength(500).IsRequired();
        builder.Property(e => e.Code).HasMaxLength(100);

        // Самоссылка иерархии (внутри схемы inspector). Restrict — родителя с детьми каскадно не удалить.
        builder.HasOne(e => e.Parent).WithMany(e => e.Children)
               .HasForeignKey(e => e.ParentId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(e => e.ParentId);
        builder.HasIndex(e => e.Code); // сопоставление с подразделением СКИД по значению (§4.2)
    }
}

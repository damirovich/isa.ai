using ISC.AI.Profile.Investigation.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ISC.AI.Profile.Investigation.Data.EntityConfigurations;

/// <summary>Конфигурация таблицы фигурантов (<c>investigation.person</c>, ТФ-ПЕР-01).</summary>
public class PersonConfiguration : IEntityTypeConfiguration<Person>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Person> builder)
    {
        builder.ToTable("person", InvestigationDbContext.Schema);
        builder.HasKey(e => e.Id);

        builder.Property(e => e.DisplayName).HasMaxLength(500).IsRequired();
        builder.Property(e => e.RoleInCase).HasMaxLength(200);
        builder.Property(e => e.Notes).HasMaxLength(4000);

        // Режимные поля денормализованы с дела (ТБ-070) и NOT NULL.
        builder.Property(e => e.Classification).IsRequired();
        builder.Property(e => e.DivisionId).IsRequired();

        // FK внутри схемы (ТО-инф-08): удаление дела уносит фигурантов.
        builder.HasOne(e => e.Case).WithMany()
               .HasForeignKey(e => e.CaseId).OnDelete(DeleteBehavior.Cascade);

        // «Неустановленное лицо № N» — номер уникален в деле; установленные (NULL) индексом не ограничены.
        builder.HasIndex(e => new { e.CaseId, e.UnidentifiedNumber })
               .IsUnique()
               .HasFilter("unidentified_number IS NOT NULL");
    }
}

using ISC.AI.Profile.Investigation.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ISC.AI.Profile.Investigation.Data.EntityConfigurations;

/// <summary>Конфигурация таблицы появлений (<c>investigation.appearance</c>, ТФ-ПЕР-02, ТБ-073).</summary>
public class AppearanceConfiguration : IEntityTypeConfiguration<Appearance>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Appearance> builder)
    {
        builder.ToTable("appearance", InvestigationDbContext.Schema);
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Classification).IsRequired();
        builder.Property(e => e.DivisionId).IsRequired();
        builder.Property(e => e.Status).IsRequired();

        // FK внутри схемы (ТО-инф-08); носитель/лицо/сессия/кандидат — по значению → схема media.
        builder.HasOne(e => e.Person).WithMany()
               .HasForeignKey(e => e.PersonId).OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => e.CaseId);
        builder.HasIndex(e => e.MediaAssetId);

        // Один кандидат — одно появление: повторное подтверждение того же кандидата не плодит записей (ТБ-073).
        builder.HasIndex(e => e.CandidateId).IsUnique();
    }
}

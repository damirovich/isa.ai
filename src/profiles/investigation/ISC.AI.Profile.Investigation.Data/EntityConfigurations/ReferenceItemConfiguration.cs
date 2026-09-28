using ISC.AI.Profile.Investigation.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ISC.AI.Profile.Investigation.Data.EntityConfigurations;

/// <summary>Конфигурация справочников профиля (<c>investigation.reference_item</c>, ТФ-АДМ-07).</summary>
public class ReferenceItemConfiguration : IEntityTypeConfiguration<ReferenceItem>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ReferenceItem> builder)
    {
        builder.ToTable("reference_item", InvestigationDbContext.Schema);
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Kind).IsRequired();
        builder.Property(e => e.Name).HasMaxLength(300).IsRequired();
        builder.Property(e => e.Code).HasMaxLength(50);

        // Наименование уникально в пределах вида: два «ГУ по борьбе с …» в одном списке неразличимы
        // для оператора. Регистр сверяет хранилище до записи; страховка от гонки двух записей «Майор» и
        // «майор» — функциональный уникальный индекс (kind, lower(name)), он создаётся SQL-ом в миграции
        // TaskRequisitesAndQuestionnaire (EF такой индекс в модели не описывает). Этот индекс — по точному имени.
        builder.HasIndex(e => new { e.Kind, e.Name }).IsUnique();
    }
}

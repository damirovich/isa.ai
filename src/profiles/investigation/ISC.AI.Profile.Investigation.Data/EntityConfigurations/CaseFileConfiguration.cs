using ISC.AI.Profile.Investigation.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ISC.AI.Profile.Investigation.Data.EntityConfigurations;

/// <summary>Конфигурация таблицы дел (<c>investigation.case_file</c>, ТФ-ДЕЛ-01).</summary>
public class CaseFileConfiguration : IEntityTypeConfiguration<CaseFile>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<CaseFile> builder)
    {
        builder.ToTable("case_file", InvestigationDbContext.Schema);
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Number).HasMaxLength(100).IsRequired();
        builder.Property(e => e.Title).HasMaxLength(500).IsRequired();
        builder.Property(e => e.Basis).HasMaxLength(2000);
        builder.Property(e => e.OpenedAt).HasColumnType("date");

        // Режимные поля — без умолчаний и NOT NULL (ТБ-024): дело без грифа/подразделения не создаётся.
        builder.Property(e => e.Classification).IsRequired();
        builder.Property(e => e.DivisionId).IsRequired();

        // Номер дела уникален в подразделении (CaseWriteResult.DuplicateNumber опирается на этот индекс).
        builder.HasIndex(e => new { e.DivisionId, e.Number }).IsUnique();

        // Решётка доступа (ТБ-020) — по грифу и подразделению; списки следователя — по InvestigatorUserId.
        builder.HasIndex(e => new { e.Classification, e.DivisionId });
        builder.HasIndex(e => e.InvestigatorUserId);
        builder.HasIndex(e => e.Status);

        // InvestigatorUserId / CreatedByUserId — слабые ссылки на core.app_user (ТО-инф-08): FK через
        // границу схем не создаётся, подразделение — тоже по значению (справочник в той же схеме, но
        // допуски ядра ссылаются на те же номера, поэтому FK намеренно нет — как у «Инспектора»).
    }
}

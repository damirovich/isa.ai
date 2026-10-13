using ISC.AI.Profile.Investigation.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ISC.AI.Profile.Investigation.Data.EntityConfigurations;

/// <summary>Конфигурация соответствий перенесённых записей баз Access (<c>investigation.access_import_record</c>, ADR-0043).</summary>
public sealed class AccessImportRecordConfiguration : IEntityTypeConfiguration<AccessImportRecordEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<AccessImportRecordEntity> builder)
    {
        builder.ToTable("access_import_record", InvestigationDbContext.Schema);
        builder.HasKey(r => r.Id);

        builder.Property(r => r.FileSha256).HasMaxLength(64).IsRequired();
        builder.Property(r => r.SourceTable).HasMaxLength(256).IsRequired();
        builder.Property(r => r.SourceKey).HasMaxLength(256).IsRequired();

        // Одна запись базы — одно соответствие: повтор переноса той же базы её пропускает.
        builder.HasIndex(r => new { r.FileSha256, r.SourceTable, r.SourceKey }).IsUnique();

        // Уничтожение дела (ADR-0025) уносит соответствия — повторный перенос заведёт дело заново.
        builder.HasOne(r => r.Case).WithMany()
               .HasForeignKey(r => r.CaseId).OnDelete(DeleteBehavior.Cascade);
    }
}

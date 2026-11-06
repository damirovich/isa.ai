using ISC.AI.Profile.Investigation.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ISC.AI.Profile.Investigation.Data.EntityConfigurations;

/// <summary>Журнал выгрузок пакетов обмена (ADR-0068).</summary>
public sealed class ExchangeExportConfiguration : IEntityTypeConfiguration<ExchangeExport>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ExchangeExport> builder)
    {
        builder.ToTable("exchange_export", InvestigationDbContext.Schema);
        builder.HasKey(e => e.Id);

        builder.Property(e => e.CaseNumbers).HasMaxLength(4000);
        builder.Property(e => e.MediaKinds).HasMaxLength(100).IsRequired();
        builder.Property(e => e.PackageName).HasMaxLength(200);
        builder.Property(e => e.Error).HasMaxLength(2000);

        // Журнал — свежие сверху; «идёт ли выгрузка» — по состоянию.
        builder.HasIndex(e => e.StartedAt);
        builder.HasIndex(e => e.Status);
    }
}

/// <summary>Файлы носителей, ушедшие успешными выгрузками (ADR-0068).</summary>
public sealed class ExchangeExportFileConfiguration : IEntityTypeConfiguration<ExchangeExportFile>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ExchangeExportFile> builder)
    {
        builder.ToTable("exchange_export_file", InvestigationDbContext.Schema);
        builder.HasKey(f => new { f.ExportId, f.AssetId });

        // «Уходил ли файл раньше» — по носителю.
        builder.HasIndex(f => f.AssetId);
        builder.HasOne<ExchangeExport>().WithMany().HasForeignKey(f => f.ExportId).OnDelete(DeleteBehavior.Cascade);
    }
}

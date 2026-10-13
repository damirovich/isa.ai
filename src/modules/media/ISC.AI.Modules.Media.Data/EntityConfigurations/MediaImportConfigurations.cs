using ISC.AI.Modules.Media.Data.Entities;
using ISC.AI.Modules.Media.Domain.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ISC.AI.Modules.Media.Data.EntityConfigurations;

/// <summary>Конфигурация заданий пакетного импорта (<c>media.import_job</c>, ADR-0041).</summary>
public sealed class MediaImportJobConfiguration : IEntityTypeConfiguration<MediaImportJob>
{
    /// <summary>Предел длины пути папки архива.</summary>
    public const int FolderPathMaxLength = MediaImportLimits.FolderPathMaxLength;

    /// <summary>Предел длины причины остановки.</summary>
    public const int StopReasonMaxLength = MediaImportLimits.ReasonMaxLength;

    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<MediaImportJob> builder)
    {
        builder.ToTable("import_job", MediaDbContext.Schema);
        builder.HasKey(j => j.Id);

        builder.Property(j => j.FolderPath).HasMaxLength(FolderPathMaxLength).IsRequired();
        builder.Property(j => j.ManifestFileName).HasMaxLength(260).IsRequired();
        builder.Property(j => j.ManifestSha256).HasMaxLength(64).IsRequired();
        builder.Property(j => j.StopReason).HasMaxLength(StopReasonMaxLength);

        // Исполнитель берёт идущее или самое раннее ждущее задание.
        builder.HasIndex(j => new { j.Status, j.Id });
    }
}

/// <summary>Конфигурация строк заданий импорта (<c>media.import_item</c>, ADR-0041).</summary>
public sealed class MediaImportItemConfiguration : IEntityTypeConfiguration<MediaImportItem>
{
    /// <summary>Предел длины пути файла относительно папки задания.</summary>
    public const int RelativePathMaxLength = MediaImportLimits.RelativePathMaxLength;

    /// <summary>Предел длины причины отказа.</summary>
    public const int ReasonMaxLength = MediaImportLimits.ReasonMaxLength;

    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<MediaImportItem> builder)
    {
        builder.ToTable("import_item", MediaDbContext.Schema);
        builder.HasKey(i => i.Id);

        builder.Property(i => i.RelativePath).HasMaxLength(RelativePathMaxLength).IsRequired();
        builder.Property(i => i.ContentType).HasMaxLength(100).IsRequired();
        builder.Property(i => i.CaseNumber).HasMaxLength(100).IsRequired();
        builder.Property(i => i.Source).HasMaxLength(1000);
        builder.Property(i => i.Place).HasMaxLength(500);
        builder.Property(i => i.ExpectedSha256).HasMaxLength(64);
        builder.Property(i => i.ContentSha256).HasMaxLength(64);
        builder.Property(i => i.Reason).HasMaxLength(ReasonMaxLength);

        // Режимные поля обязательны (ТБ-070): строка без грифа и подразделения дела в схему не попадает.
        builder.Property(i => i.Classification).IsRequired();
        builder.Property(i => i.DivisionId).IsRequired();

        // Строка описи в задании одна; исполнитель берёт строки по порядку описи и по состоянию.
        builder.HasIndex(i => new { i.JobId, i.RowNumber }).IsUnique();
        builder.HasIndex(i => new { i.JobId, i.Status, i.RowNumber });

        // Подача в обработку берёт «ждёт подачи» и «в обработке» по порядку заданий и строк; сводка считает строки
        // задания по состоянию подачи — тот же индекс.
        builder.HasIndex(i => new { i.Processing, i.JobId, i.RowNumber });
        builder.HasIndex(i => i.AssetId).HasFilter("asset_id IS NOT NULL");
        builder.HasIndex(i => new { i.Classification, i.DivisionId });

        builder.HasOne(i => i.Job).WithMany(j => j.Items)
               .HasForeignKey(i => i.JobId).OnDelete(DeleteBehavior.Cascade);

        // Удаление носителя (ТБ-075) не блокируется историей импорта: ссылка обнуляется, строка остаётся.
        builder.HasOne(i => i.Asset).WithMany()
               .HasForeignKey(i => i.AssetId).OnDelete(DeleteBehavior.SetNull);
    }
}

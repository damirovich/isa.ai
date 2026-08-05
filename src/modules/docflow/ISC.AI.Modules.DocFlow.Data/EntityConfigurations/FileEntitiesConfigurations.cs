using ISC.AI.Modules.DocFlow.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ISC.AI.Modules.DocFlow.Data.EntityConfigurations;

/// <summary>
/// Конфигурации файловых таблиц модуля (ТЗ СКИД §3.3, §4.2, §4.6) — четыре однотипные сущности
/// «файл при родителе» собраны в одном файле намеренно: форма идентична, различие — родитель.
/// Дочерние файлы удаляются каскадом вместе с родителем (внутрисхемные FK).
/// </summary>
public class DocumentFileConfiguration : IEntityTypeConfiguration<DocumentFile>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<DocumentFile> builder)
    {
        builder.ToTable("document_file", DocFlowDbContext.Schema);
        ConfigureFileColumns(builder, f => f.FileName, f => f.StoredFileName, f => f.ContentType);

        builder.HasOne<Document>().WithMany(d => d.Files)
            .HasForeignKey(f => f.DocumentId).OnDelete(DeleteBehavior.Cascade);

        // Быстрый доступ к актуальной версии (§3.3: история версий + текущая).
        builder.HasIndex(f => new { f.DocumentId, f.IsLatest });
        builder.Property(f => f.PdfCopyStoredFileName).HasMaxLength(500);
    }

    /// <summary>Общие ограничения файловых колонок (имена/тип; вызывается каждой конфигурацией).</summary>
    internal static void ConfigureFileColumns<T>(
        EntityTypeBuilder<T> builder,
        System.Linq.Expressions.Expression<Func<T, string>> fileName,
        System.Linq.Expressions.Expression<Func<T, string>> storedFileName,
        System.Linq.Expressions.Expression<Func<T, string>> contentType)
        where T : class
    {
        builder.Property(fileName).HasMaxLength(500).IsRequired();
        builder.Property(storedFileName).HasMaxLength(500).IsRequired();
        builder.Property(contentType).HasMaxLength(200).IsRequired();
    }
}

/// <inheritdoc cref="DocumentFileConfiguration" />
public class DocumentAttachmentConfiguration : IEntityTypeConfiguration<DocumentAttachment>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<DocumentAttachment> builder)
    {
        builder.ToTable("document_attachment", DocFlowDbContext.Schema);
        DocumentFileConfiguration.ConfigureFileColumns(
            builder, f => f.FileName, f => f.StoredFileName, f => f.ContentType);

        builder.HasOne<Document>().WithMany()
            .HasForeignKey(f => f.DocumentId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(f => f.DocumentId);
    }
}

/// <inheritdoc cref="DocumentFileConfiguration" />
public class StatusHistoryFileConfiguration : IEntityTypeConfiguration<StatusHistoryFile>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<StatusHistoryFile> builder)
    {
        builder.ToTable("status_history_file", DocFlowDbContext.Schema);
        DocumentFileConfiguration.ConfigureFileColumns(
            builder, f => f.FileName, f => f.StoredFileName, f => f.ContentType);

        builder.HasOne(f => f.StatusHistory).WithMany()
            .HasForeignKey(f => f.StatusHistoryId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(f => f.StatusHistoryId);
    }
}

/// <inheritdoc cref="DocumentFileConfiguration" />
public class DeadlineExtensionFileConfiguration : IEntityTypeConfiguration<DeadlineExtensionFile>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<DeadlineExtensionFile> builder)
    {
        builder.ToTable("deadline_extension_file", DocFlowDbContext.Schema);
        DocumentFileConfiguration.ConfigureFileColumns(
            builder, f => f.FileName, f => f.StoredFileName, f => f.ContentType);

        builder.HasOne(f => f.Extension).WithMany()
            .HasForeignKey(f => f.ExtensionId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(f => f.ExtensionId);
    }
}

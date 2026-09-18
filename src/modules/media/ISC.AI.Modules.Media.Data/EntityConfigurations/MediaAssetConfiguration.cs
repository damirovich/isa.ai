using ISC.AI.Modules.Media.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ISC.AI.Modules.Media.Data.EntityConfigurations;

/// <summary>Конфигурация таблицы носителей (<c>media.asset</c>).</summary>
public sealed class MediaAssetConfiguration : IEntityTypeConfiguration<MediaAsset>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<MediaAsset> builder)
    {
        builder.ToTable("asset", MediaDbContext.Schema);
        builder.HasKey(a => a.Id);

        builder.Property(a => a.OriginalFileName).HasMaxLength(260).IsRequired();
        builder.Property(a => a.StoredFileName).HasMaxLength(64).IsRequired();
        builder.Property(a => a.ContentType).HasMaxLength(100).IsRequired();
        builder.Property(a => a.ContentHash).HasMaxLength(64).IsRequired();
        builder.Property(a => a.Source).HasMaxLength(500);
        builder.Property(a => a.IndexError).HasMaxLength(2000);
        builder.Property(a => a.DetectorVersion).HasMaxLength(50);
        builder.Property(a => a.EmbedderVersion).HasMaxLength(50);

        // Режимные поля обязательны (ТБ-020): носитель без грифа в схему не попадает.
        builder.Property(a => a.Classification).IsRequired();
        builder.Property(a => a.DivisionId).IsRequired();
        builder.Property(a => a.IsCurrent).IsRequired().HasDefaultValue(true);

        // Дедупликация: один и тот же файл в одном подразделении ПОД ОДНИМ ГРИФОМ хранится один раз (ТБ-074).
        // Гриф входит в ключ: гриф носителя = гриф дела (ТБ-070), и дубликат под другим грифом — отдельная
        // строка/копия, иначе носитель наследовал бы режим первого дела (и «исчезал» для следователя второго
        // либо оставлял биометрию секретного дела под низким грифом).
        builder.HasIndex(a => new { a.DivisionId, a.Classification, a.ContentHash }).IsUnique();
        builder.HasIndex(a => a.StoredFileName).IsUnique();
        builder.HasIndex(a => new { a.Classification, a.DivisionId });
        builder.HasIndex(a => a.IndexStatus);
    }
}

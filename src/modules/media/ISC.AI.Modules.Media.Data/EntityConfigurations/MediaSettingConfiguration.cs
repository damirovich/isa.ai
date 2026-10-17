using ISC.AI.Modules.Media.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ISC.AI.Modules.Media.Data.EntityConfigurations;

/// <summary>Настройки пакета (<c>media.setting</c>, ADR-0054): «ключ — значение».</summary>
public sealed class MediaSettingConfiguration : IEntityTypeConfiguration<MediaSetting>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<MediaSetting> builder)
    {
        System.ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("setting", MediaDbContext.Schema);

        // Ключ — естественный первичный: суррогатный идентификатор позволил бы завести две строки на одну настройку.
        builder.HasKey(s => s.Key);
        builder.Property(s => s.Key).HasMaxLength(100);
        builder.Property(s => s.Value).HasMaxLength(500);
    }
}

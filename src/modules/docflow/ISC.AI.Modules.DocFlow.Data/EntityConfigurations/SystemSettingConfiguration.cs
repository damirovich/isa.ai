using ISC.AI.Modules.DocFlow.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ISC.AI.Modules.DocFlow.Data.EntityConfigurations;

/// <summary>Системные настройки модуля (§9): «ключ-значение» в схеме <c>docflow</c>.</summary>
public sealed class SystemSettingConfiguration : IEntityTypeConfiguration<SystemSetting>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<SystemSetting> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("system_setting");

        // Ключ — естественный первичный: суррогатный Id здесь только мешал бы, добавляя вторую
        // возможность завести две строки на один и тот же параметр.
        builder.HasKey(s => s.Key);
        builder.Property(s => s.Key).HasMaxLength(100);
        builder.Property(s => s.Value).HasMaxLength(500);
    }
}

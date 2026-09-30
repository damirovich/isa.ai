using ISC.AI.Profile.Inspector.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ISC.AI.Profile.Inspector.Data.EntityConfigurations;

/// <summary>Конфигурация матрицы доступа (<c>inspector.role_permission</c>, ADR-0033).</summary>
public class RolePermissionConfiguration : IEntityTypeConfiguration<RolePermission>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<RolePermission> builder)
    {
        builder.ToTable("role_permission", InspectorDbContext.Schema);
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Permission).HasMaxLength(64).IsRequired();

        // Одна ячейка — одна строка: повторное сохранение правит ту же запись, а не добавляет вторую.
        builder.HasIndex(e => new { e.Role, e.Permission }).IsUnique();
    }
}

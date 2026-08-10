using ISC.AI.Profile.Inspector.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ISC.AI.Profile.Inspector.Data.EntityConfigurations;

/// <summary>Конфигурация таблицы ролей пользователей (<c>inspector.user_role_assignment</c>, §2.1 ТЗ СКИД).</summary>
public class UserRoleAssignmentConfiguration : IEntityTypeConfiguration<UserRoleAssignment>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<UserRoleAssignment> builder)
    {
        builder.ToTable("user_role_assignment", InspectorDbContext.Schema);
        builder.HasKey(e => e.Id);

        // Один пользователь — одна роль; UserId — слабая ссылка на core.app_user.Id (ТО-инф-06).
        builder.HasIndex(e => e.UserId).IsUnique();
    }
}

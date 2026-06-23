using System.Linq.Expressions;

namespace ISC.AI.Persistence;

/// <summary>
/// Базовый контекст с общим механизмом аудита: мягкое удаление, авто-таймстемпы и глобальный
/// query-filter <c>!IsDeleted</c>. Переиспользуется обоими контекстами (<see cref="CoreDbContext"/> —
/// схема <c>core</c>; <c>InspectorDbContext</c> профиля — схема <c>inspector</c>), чтобы единый
/// механизм захвата аудита (Э3-03) и таймстемпов не дублировался и не расходился между контекстами.
/// </summary>
public abstract class AuditedDbContext : DbContext
{
    /// <summary>Создаёт контекст с заданными настройками EF Core.</summary>
    protected AuditedDbContext(DbContextOptions options) : base(options)
    {
    }

    /// <inheritdoc />
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ApplyAudit();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    /// <inheritdoc />
    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        ApplyAudit();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    // Мягкое удаление и авто-таймстемпы. Захват diff для неизменяемого аудита — на Э3-03
    // (журнала в этой БД пока нет; копится в sink). Перехват в обоих SaveChanges-путях, чтобы
    // аудит нельзя было обойти синхронным вызовом.
    private void ApplyAudit()
    {
        ApplySoftDelete();
        ApplyAuditableTimestamps();
    }

    private void ApplyAuditableTimestamps()
    {
        var entries = ChangeTracker.Entries()
            .Where(e => e.Entity is IAuditableEntity &&
                        (e.State == EntityState.Added || e.State == EntityState.Modified));

        foreach (var entry in entries)
        {
            var auditable = (IAuditableEntity)entry.Entity;
            auditable.UpdatedAt = DateTime.UtcNow;

            if (entry.State == EntityState.Added)
            {
                auditable.CreatedAt = DateTime.UtcNow;
            }
        }
    }

    private void ApplySoftDelete()
    {
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State == EntityState.Deleted && entry.Entity is ISoftDeletable softDeletable)
            {
                entry.State = EntityState.Modified;
                softDeletable.IsDeleted = true;
            }
        }
    }

    /// <summary>
    /// Регистрирует глобальный query-filter мягкого удаления (<c>!IsDeleted</c>) для всех
    /// <see cref="ISoftDeletable"/> модели. Вызывать из <c>OnModelCreating</c> ПОСЛЕ применения
    /// конфигураций сущностей (иначе в модели ещё нет типов).
    /// </summary>
    protected static void ApplySoftDeleteQueryFilters(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (typeof(ISoftDeletable).IsAssignableFrom(entityType.ClrType))
            {
                var parameter = Expression.Parameter(entityType.ClrType, "e");
                var isDeleted = Expression.Property(parameter, nameof(ISoftDeletable.IsDeleted));
                var notDeleted = Expression.Equal(isDeleted, Expression.Constant(false));
                modelBuilder.Entity(entityType.ClrType).HasQueryFilter(Expression.Lambda(notDeleted, parameter));
            }
        }
    }
}

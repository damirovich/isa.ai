using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Identity.Skid;

/// <summary>
/// Read-only контекст БД СКИД — ТОЛЬКО чтение идентичности (пользователи, подразделения).
/// </summary>
/// <remarks>
/// ИНВАРИАНТ (ТД-004): ISC.AI не владеет схемой СКИД — никаких миграций, записи и трекинга; учётка
/// подключения должна быть выдана с правами только на SELECT (GRANT-уровень — вторая линия защиты).
/// Трекинг отключён на уровне контекста, <see cref="SaveChanges()"/> перекрыт отказом.
/// </remarks>
public class SkidDbContext(DbContextOptions<SkidDbContext> options) : DbContext(options)
{
    /// <summary>Пользователи СКИД (<c>public.users</c>).</summary>
    public DbSet<SkidUserRow> Users => Set<SkidUserRow>();

    /// <summary>Подразделения СКИД (<c>public.departments</c>).</summary>
    public DbSet<SkidDepartmentRow> Departments => Set<SkidDepartmentRow>();

    /// <inheritdoc />
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        // Чтение без трекинга: контекст никогда не пишет в чужую БД.
        optionsBuilder.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
        base.OnConfiguring(optionsBuilder);
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SkidUserRow>(user =>
        {
            user.ToTable("users");
            user.HasKey(u => u.Id);
        });

        modelBuilder.Entity<SkidDepartmentRow>(department =>
        {
            department.ToTable("departments");
            department.HasKey(d => d.Id);
        });
    }

    // Перекрыты ОБЕ пары перегрузок: беспараметрические SaveChanges()/SaveChangesAsync(CancellationToken)
    // в EF Core — тонкие делегаты к SaveChanges(bool)/SaveChangesAsync(bool, CancellationToken); если
    // перекрыть только первые, вызов с явным acceptAllChangesOnSuccess пройдёт мимо запрета.

    /// <inheritdoc />
    public override int SaveChanges(bool acceptAllChangesOnSuccess) =>
        throw new InvalidOperationException("Контекст СКИД — только для чтения: запись в чужую БД запрещена.");

    /// <inheritdoc />
    public override int SaveChanges() =>
        throw new InvalidOperationException("Контекст СКИД — только для чтения: запись в чужую БД запрещена.");

    /// <inheritdoc />
    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Контекст СКИД — только для чтения: запись в чужую БД запрещена.");

    /// <inheritdoc />
    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Контекст СКИД — только для чтения: запись в чужую БД запрещена.");
}

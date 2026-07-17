namespace ISC.AI.Profile.ERP.Data;

/// <summary>
/// Доменный контекст профиля «АИС ЕРП» (схема <c>erp</c>). КАРКАС: сущностей пока нет — состав
/// определится по деталям проекта и контракту API АИС ЕРП. Ведёт собственную историю миграций
/// (<c>erp.__ef_migrations_history</c>, ADR-0003, ТО-инф-01).
/// </summary>
/// <remarks>
/// Наследует общий механизм аудита/таймстемпов/soft-delete у ядра (<see cref="AuditedDbContext"/>), чтобы
/// аудит не расходился между контекстами. Конфигурации — отдельными классами
/// <see cref="IEntityTypeConfiguration{T}"/> (папка <c>EntityConfigurations</c>). FK создаются только ВНУТРИ
/// схемы <c>erp</c>; связи с <c>core</c> и с внешней АИС ЕРП — слабые ПО ЗНАЧЕНИЮ, без FK через границу
/// (ТО-инф-06). Под Blazor Server создаётся через <c>IDbContextFactory</c> (ТС-008).
/// </remarks>
public class ErpDbContext(DbContextOptions<ErpDbContext> options) : AuditedDbContext(options)
{
    /// <summary>Имя схемы профиля.</summary>
    public const string Schema = "erp";

    // DbSet'ы доменных сущностей ЕРП появятся при уточнении домена (преступление, эпизод, лицо, дело).

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.ApplyConfigurationsFromAssembly(
            typeof(ErpDbContext).Assembly,
            type => type.Namespace?.Contains("EntityConfigurations", StringComparison.Ordinal) == true);

        ApplySoftDeleteQueryFilters(modelBuilder);
    }
}

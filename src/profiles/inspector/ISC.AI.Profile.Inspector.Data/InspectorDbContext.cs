namespace ISC.AI.Profile.Inspector.Data;

/// <summary>
/// Доменный контекст профиля «ИнспекторAI» (схема <c>inspector</c>): модель НПА (норма, редакция)
/// и связки с нейтральным корпусом ядра. Ведёт собственную историю миграций
/// (<c>inspector.__ef_migrations_history</c>, ADR-0003, ТО-инф-01).
/// </summary>
/// <remarks>
/// Наследует общий механизм аудита/таймстемпов/soft-delete у ядра (<see cref="AuditedDbContext"/>),
/// чтобы аудит не расходился между контекстами. Конфигурации — отдельными классами
/// <see cref="IEntityTypeConfiguration{T}"/> (папка <c>EntityConfigurations</c>). FK создаются только
/// ВНУТРИ схемы <c>inspector</c>; связи с <c>core</c> — слабые по значению идентификатора, без FK
/// через границу схем (ТО-инф-06). Под Blazor Server создаётся через <c>IDbContextFactory</c> (ТС-008).
/// </remarks>
public class InspectorDbContext(DbContextOptions<InspectorDbContext> options) : AuditedDbContext(options)
{
    /// <summary>Имя схемы профиля.</summary>
    public const string Schema = "inspector";

    public DbSet<LegalNorm> LegalNorms { get; set; } = null!;
    public DbSet<NormRevision> NormRevisions { get; set; } = null!;
    public DbSet<NormDocumentLink> NormDocumentLinks { get; set; } = null!;
    public DbSet<ChunkRevisionLink> ChunkRevisionLinks { get; set; } = null!;

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.ApplyConfigurationsFromAssembly(
            typeof(InspectorDbContext).Assembly,
            type => type.Namespace?.Contains("EntityConfigurations", StringComparison.Ordinal) == true);

        ApplySoftDeleteQueryFilters(modelBuilder);
    }
}

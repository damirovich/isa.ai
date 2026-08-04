namespace ISC.AI.Persistence;

/// <summary>
/// Универсальный, доменно-нейтральный контекст данных ядра (схема <c>core</c>): generic-корпус
/// (документы, чанки, эмбеддинги), пользователи и допуски, задания индексации. Доменных понятий
/// (НПА, нормы, редакции) НЕ содержит — они в профиле (схема <c>inspector</c>, <c>InspectorDbContext</c>).
/// </summary>
/// <remarks>
/// Конфигурации сущностей вынесены в отдельные классы <see cref="IEntityTypeConfiguration{T}"/>
/// (папка <c>EntityConfigurations</c>) и подключаются <c>ApplyConfigurationsFromAssembly</c>.
/// Мягкое удаление, авто-таймстемпы и query-filter <c>!IsDeleted</c> — общие, в базовом
/// <see cref="AuditedDbContext"/>. Ведёт собственную историю миграций (<c>core.__ef_migrations_history</c>);
/// под Blazor Server создаётся через <c>IDbContextFactory</c> (ТС-008). Векторное хранилище
/// (pgvector) — Э3-02. FK через границу схем <c>core</c>/<c>inspector</c> не создаются (ТО-инф-06).
/// </remarks>
public class CoreDbContext(DbContextOptions<CoreDbContext> options) : AuditedDbContext(options)
{
    /// <summary>Имя схемы ядра.</summary>
    public const string Schema = "core";

    public DbSet<DocumentEntity> Documents { get; set; } = null!;
    public DbSet<ChunkEntity> Chunks { get; set; } = null!;
    public DbSet<EmbeddingEntity> Embeddings { get; set; } = null!;
    public DbSet<AppUserEntity> Users { get; set; } = null!;
    public DbSet<ClearanceEntity> Clearances { get; set; } = null!;
    public DbSet<IndexingJobEntity> IndexingJobs { get; set; } = null!;
    public DbSet<AuditRecordEntity> AuditRecords { get; set; } = null!;
    public DbSet<BackgroundTaskEntity> BackgroundTasks { get; set; } = null!;
    public DbSet<ConversationEntity> Conversations { get; set; } = null!;
    public DbSet<ConversationMessageEntity> ConversationMessages { get; set; } = null!;

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasDefaultSchema(Schema);

        // Векторное хранилище pgvector (ТО-инф-02): расширение создаётся в миграции (CREATE EXTENSION vector).
        modelBuilder.HasPostgresExtension("vector");

        // Конфигурации сущностей — отдельными классами IEntityTypeConfiguration (папка EntityConfigurations).
        modelBuilder.ApplyConfigurationsFromAssembly(
            typeof(CoreDbContext).Assembly,
            type => type.Namespace?.Contains("EntityConfigurations", StringComparison.Ordinal) == true);

        // Глобальный query-filter мягкого удаления — после применения конфигураций (см. базовый класс).
        ApplySoftDeleteQueryFilters(modelBuilder);
    }
}

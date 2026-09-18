using ISC.AI.Modules.Media.Data.Entities;
using ISC.AI.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Modules.Media.Data;

/// <summary>
/// Контекст данных пакета «Медиа» (схема <c>media</c>, ДОК-13 §5.2): носители, кадры, лица, шаблоны,
/// поисковые сессии с кандидат-листами и решениями верификации (ТО-инф-12, ТБ-073).
/// Ведёт собственную историю миграций (<c>media.__ef_migrations_history</c>, ADR-0017, ТО-инф-08).
/// </summary>
/// <remarks>
/// Наследует общий механизм таймстемпов у ядра (<see cref="AuditedDbContext"/>). Мягкого удаления в
/// схеме НЕТ намеренно: биометрия удаляется физически (ТБ-064/075), каскадом FK внутри схемы. Связь с
/// другими схемами (пользователи ядра, дела профиля) — СЛАБАЯ, по значению идентификатора, без FK
/// через границу схем (ТО-инф-06). Под Blazor Server создаётся через <c>IDbContextFactory</c> (ТС-008).
/// Расширение <c>vector</c> объявляется здесь же: пакет обязан работать и на БД, где ядро ещё не
/// накатывалось (общая БД — соглашение развёртывания, не зависимость).
/// </remarks>
public class MediaDbContext(DbContextOptions<MediaDbContext> options) : AuditedDbContext(options)
{
    /// <summary>Имя схемы пакета «Медиа».</summary>
    public const string Schema = "media";

    /// <summary>Носители (фото/видео).</summary>
    public DbSet<MediaAsset> Assets { get; set; } = null!;

    /// <summary>Кадры видео с лицами.</summary>
    public DbSet<MediaFrame> Frames { get; set; } = null!;

    /// <summary>Найденные лица.</summary>
    public DbSet<Face> Faces { get; set; } = null!;

    /// <summary>Шаблоны лиц (векторы).</summary>
    public DbSet<FaceTemplate> Templates { get; set; } = null!;

    /// <summary>Поисковые сессии (ТО-инф-12).</summary>
    public DbSet<SearchSession> SearchSessions { get; set; } = null!;

    /// <summary>Кандидаты поисковых сессий (ТФ-ПЛ-02).</summary>
    public DbSet<SearchCandidate> SearchCandidates { get; set; } = null!;

    /// <summary>Решения верификации (ТБ-073).</summary>
    public DbSet<VerificationDecisionEntity> VerificationDecisions { get; set; } = null!;

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.HasPostgresExtension("vector");

        modelBuilder.ApplyConfigurationsFromAssembly(
            typeof(MediaDbContext).Assembly,
            type => type.Namespace?.Contains("EntityConfigurations", StringComparison.Ordinal) == true);

        ApplySoftDeleteQueryFilters(modelBuilder);
    }
}

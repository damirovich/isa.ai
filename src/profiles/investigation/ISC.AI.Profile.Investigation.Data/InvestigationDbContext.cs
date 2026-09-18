using ISC.AI.Persistence;
using ISC.AI.Profile.Investigation.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Profile.Investigation.Data;

/// <summary>
/// Доменный контекст профиля «Следствие» (схема <c>investigation</c>): дела, фигуранты, эталоны,
/// появления, основания поиска, привязки носителей/документов, справочник подразделений и роли.
/// Ведёт собственную историю миграций (<c>investigation.__ef_migrations_history</c>, ADR-0003, ТО-инф-01).
/// </summary>
/// <remarks>
/// Наследует общий механизм таймстемпов/soft-delete у ядра (<see cref="AuditedDbContext"/>). FK создаются
/// только ВНУТРИ схемы <c>investigation</c>; ссылки в <c>core</c> (пользователи), <c>media</c> (носители,
/// лица, сессии, кандидаты) и <c>docflow</c> (документы) — слабые, по значению идентификатора (ТО-инф-08).
/// Без pgvector: шаблоны лиц живут в схеме <c>media</c>, профиль векторов не хранит (ТБ-076).
/// Под Blazor Server создаётся через <c>IDbContextFactory</c> (ТС-008).
/// </remarks>
public class InvestigationDbContext(DbContextOptions<InvestigationDbContext> options) : AuditedDbContext(options)
{
    /// <summary>Имя схемы профиля.</summary>
    public const string Schema = "investigation";

    /// <summary>Дела (ТФ-ДЕЛ-01).</summary>
    public DbSet<CaseFile> Cases { get; set; } = null!;

    /// <summary>Фигуранты (ТФ-ПЕР-01).</summary>
    public DbSet<Person> Persons { get; set; } = null!;

    /// <summary>Эталонные изображения фигурантов (ТБ-077).</summary>
    public DbSet<ReferencePhoto> ReferencePhotos { get; set; } = null!;

    /// <summary>Подтверждённые появления (ТФ-ПЕР-02, ТБ-073).</summary>
    public DbSet<Appearance> Appearances { get; set; } = null!;

    /// <summary>Привязки носителей к делам (ТФ-ДЕЛ-02).</summary>
    public DbSet<CaseMediaLink> CaseMediaLinks { get; set; } = null!;

    /// <summary>Привязки документов документооборота к делам.</summary>
    public DbSet<CaseDocumentLink> CaseDocumentLinks { get; set; } = null!;

    /// <summary>Основания поиска (ТБ-071).</summary>
    public DbSet<SearchAuthorization> SearchAuthorizations { get; set; } = null!;

    /// <summary>Акты об удалении шаблонов по закрытию дел (ТФ-ДЕЛ-04, ТБ-074).</summary>
    public DbSet<CaseClosureAct> ClosureActs { get; set; } = null!;

    /// <summary>Справочник подразделений (ТФ-АДМ-01).</summary>
    public DbSet<Division> Divisions { get; set; } = null!;

    /// <summary>Роли пользователей (ТП-004).</summary>
    public DbSet<UserRoleAssignment> UserRoleAssignments { get; set; } = null!;

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.ApplyConfigurationsFromAssembly(
            typeof(InvestigationDbContext).Assembly,
            type => type.Namespace?.Contains("EntityConfigurations", StringComparison.Ordinal) == true);

        ApplySoftDeleteQueryFilters(modelBuilder);
    }
}

using System;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Data;
using ISC.AI.Profile.Investigation.Domain.Entities;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Фабрика <see cref="InvestigationDbContext"/> (схема профиля «Следствие»). Без pgvector: профиль
/// векторов не хранит (ТБ-076), шаблоны лиц живут в схеме <c>media</c>.
/// </summary>
internal sealed class InvestigationContextFactory(string connectionString) : IDbContextFactory<InvestigationDbContext>
{
    public InvestigationDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<InvestigationDbContext>()
            .UseNpgsql(connectionString, npg =>
                npg.MigrationsHistoryTable("__ef_migrations_history", InvestigationDbContext.Schema))
            .UseSnakeCaseNamingConvention()
            .Options);
}

/// <summary>
/// Общая обвязка тестов профиля «Следствие»: миграция схемы, назначение ролей, сборка хранилищ с
/// настоящей политикой и реестром ролей — как в хосте, без DI-контейнера.
/// </summary>
internal static class InvestigationTestKit
{
    /// <summary>Накатывает миграции схемы <c>investigation</c>.</summary>
    public static async Task MigrateAsync(InvestigationContextFactory factory)
    {
        await using var db = factory.CreateDbContext();
        await db.Database.MigrateAsync();
    }

    /// <summary>Назначает роли пользователям (напрямую в таблицу — предмет тестов не ведение ролей).</summary>
    public static async Task AssignRolesAsync(InvestigationContextFactory factory, params (int UserId, InvestigationRole Role)[] roles)
    {
        await using var db = factory.CreateDbContext();
        foreach (var (userId, role) in roles)
        {
            db.UserRoleAssignments.Add(new UserRoleAssignment { UserId = userId, Role = role });
        }

        await db.SaveChangesAsync();
    }

    /// <summary>Хранилище дел с настоящей политикой профиля и реестром ролей (ядро не нужно: GetRoleAsync читает только схему профиля).</summary>
    public static CaseStore CreateCaseStore(InvestigationContextFactory factory, CoreContextFactory core) =>
        new(factory, new InvestigationAccessPolicy(factory), new UserRoleStore(core, factory));

    /// <summary>Хранилище фигурантов с той же решёткой.</summary>
    public static PersonStore CreatePersonStore(InvestigationContextFactory factory, CoreContextFactory core) =>
        new(factory, new InvestigationAccessPolicy(factory), new UserRoleStore(core, factory));

    /// <summary>
    /// Порт «Медиа» <c>ICaseScope</c> поверх настоящих хранилищ, политики и реестра ролей — как в хосте.
    /// <paramref name="purgeTemplatesOnCaseClosure"/> — правило хранения биометрии эксплуатанта (ADR-0024):
    /// по умолчанию шаблоны закрытых дел ХРАНЯТСЯ, как и в поставке.
    /// </summary>
    public static CaseScope CreateCaseScope(
        InvestigationContextFactory factory, CoreContextFactory core, bool purgeTemplatesOnCaseClosure = false) =>
        new(CreateCaseStore(factory, core), CreatePersonStore(factory, core), factory,
            new InvestigationAccessPolicy(factory), new UserRoleStore(core, factory),
            new InvestigationRetentionOptions(purgeTemplatesOnCaseClosure));

    /// <summary>Контекст доступа: числовой субъект (роль ищется по нему), допуск по грифу и подразделениям.</summary>
    public static AccessContext Access(int userId, short maxClassification, params int[] divisions) =>
        new(userId.ToString(System.Globalization.CultureInfo.InvariantCulture), maxClassification, divisions);

    /// <summary>Черновик дела с обязательными режимными полями (ТБ-024).</summary>
    public static CaseDraft Draft(string number, int divisionId, short classification, int? investigatorUserId, int? createdByUserId = null) =>
        new(number, "Дело " + number, CaseKind.CriminalCase, new DateOnly(2026, 9, 1), investigatorUserId,
            divisionId, classification, Basis: null, createdByUserId);
}

/// <summary>Заглушка текущего субъекта для портов, работающих «от имени вошедшего» (<see cref="ISubjectProvider"/>).</summary>
internal sealed class FixedSubjectProvider(int? userId) : ISubjectProvider
{
    public Task<int?> GetCurrentUserIdAsync(CancellationToken cancellationToken = default) => Task.FromResult(userId);
}

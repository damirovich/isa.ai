using ISC.AI.Abstractions.Security;
using ISC.AI.Persistence;
using ISC.AI.Profile.Investigation.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ISC.AI.Profile.Investigation.Data;

/// <summary>Регистрация слоя данных профиля «Следствие» (схема <c>investigation</c>) в контейнере хоста.</summary>
public static class InvestigationPersistenceServiceCollectionExtensions
{
    /// <summary>
    /// Регистрирует <see cref="InvestigationDbContext"/> через фабрику (ТС-008) с провайдером Npgsql,
    /// хранилища профиля и реализации портов пакетов «Документооборот» и «Медиа». История миграций — в
    /// схеме <c>investigation</c>. Строка подключения — <c>ConnectionStrings:Investigation</c> (та же БД,
    /// что и ядро; при отсутствии — fallback на <c>Core</c>). Вызывать ПОСЛЕ регистрации ядра:
    /// <see cref="IAccessPolicy"/> здесь переопределяет <c>AllowAllAccessPolicy</c>.
    /// </summary>
    public static IServiceCollection AddInvestigationPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Секрет пароля — отдельно (Database:Password из user-secrets/env), в конфиге лишь несекретная база (Э4-10).
        var connectionString = ConnectionStringResolver.Resolve(configuration, "Investigation", fallbackName: "Core");

        services.AddDbContextFactory<InvestigationDbContext>(options =>
            options.UseNpgsql(connectionString, npg =>
                    npg.MigrationsHistoryTable("__ef_migrations_history", InvestigationDbContext.Schema))
                .UseSnakeCaseNamingConvention());

        // Роли (ТП-004) и справочник подразделений (ТФ-АДМ-01) — основа всех остальных проверок.
        services.AddScoped<IUserRoleStore, UserRoleStore>();
        services.AddScoped<IDivisionAdminStore, DivisionAdminStore>();

        // Дела и фигуранты (ТФ-ДЕЛ-01..03, ТФ-ПЕР-01/02): решётка ядра + CaseAccessRule на стороне БД.
        services.AddScoped<ICaseStore, CaseStore>();
        services.AddScoped<IPersonStore, PersonStore>();

        // Порты пакета «Документооборот» (ADR-0017): справочник подразделений, право настройки,
        // кандидаты в ответственные/исполнители. Без реализаций модуль fail-closed.
        services.AddScoped<ISC.AI.Modules.DocFlow.Domain.Services.IDivisionDirectory, InvestigationDivisionDirectory>();
        services.AddScoped<ISC.AI.Modules.DocFlow.Domain.Services.IDocFlowAdministration, InvestigationDocFlowAdministration>();
        services.AddScoped<ISC.AI.Modules.DocFlow.Domain.Services.IAssignmentCandidateDirectory, InvestigationAssignmentCandidateDirectory>();

        // Порты пакета «Медиа»: область дел субъекта (ТБ-071), права по роли (ТП-004), стадии
        // верификации (ТБ-073). Без ICaseScope приложение стартовать не должно (ТС-013).
        services.AddScoped<ISC.AI.Modules.Media.Domain.Services.ICaseScope, CaseScope>();
        services.AddScoped<ISC.AI.Modules.Media.Domain.Services.IMediaAdministration, MediaAdministration>();
        services.AddScoped<ISC.AI.Modules.Media.Domain.Services.IVerificationPolicy, VerificationPolicy>();

        // Сверка допусков со справочником подразделений на старте (только лог).
        services.AddHostedService<ClearanceDivisionConsistencyCheck>();

        // Переопределяет AllowAllAccessPolicy ядра (ядро регистрируется РАНЬШЕ — Program.cs) явной
        // повторной регистрацией. Singleton — как у дефолта; IDbContextFactory потокобезопасен,
        // конкретный DbContext создаётся заново на каждый вызов BuildFilter.
        services.AddSingleton<IAccessPolicy, InvestigationAccessPolicy>();

        return services;
    }
}

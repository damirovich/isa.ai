using ISC.AI.Persistence;
using ISC.AI.Profile.Inspector.Domain.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ISC.AI.Profile.Inspector.Data;

/// <summary>Регистрация доменного слоя данных профиля (схема <c>inspector</c>) в контейнере хоста.</summary>
public static class InspectorPersistenceServiceCollectionExtensions
{
    /// <summary>
    /// Регистрирует <see cref="InspectorDbContext"/> через фабрику (ТС-008) с провайдером Npgsql.
    /// История миграций — в схеме <c>inspector</c>. Строка подключения — секция
    /// <c>ConnectionStrings:Inspector</c> (та же БД, что и ядро; при отсутствии — fallback на <c>Core</c>).
    /// </summary>
    public static IServiceCollection AddInspectorPersistence(
        this IServiceCollection services, IConfiguration configuration)
    {
        // Секрет пароля — отдельно (Database:Password из user-secrets/env), в конфиге лишь несекретная база (Э4-10).
        var connectionString = ConnectionStringResolver.Resolve(configuration, "Inspector", fallbackName: "Core");

        services.AddDbContextFactory<InspectorDbContext>(options =>
            options.UseNpgsql(connectionString, npg =>
                    npg.MigrationsHistoryTable("__ef_migrations_history", InspectorDbContext.Schema))
                .UseSnakeCaseNamingConvention());

        // Материализация статуса редакции НПА → флаг годности ядра по связкам (Э4-02, ADR-0013).
        services.AddScoped<IRevisionStatusMaterializer, RevisionStatusMaterializer>();

        // Профиль отдаёт модулю документооборота свой справочник подразделений (вопрос 3 Э4-35):
        // словарь id един с решёткой доступа, справочник ведёт профиль.
        services.AddScoped<ISC.AI.Modules.DocFlow.Domain.Services.IDivisionDirectory, DocFlowDivisionDirectory>();

        // Ведение справочника подразделений (§4.2) — страница «Территориальные».
        services.AddScoped<IDivisionAdminStore, DivisionAdminStore>();

        return services;
    }
}

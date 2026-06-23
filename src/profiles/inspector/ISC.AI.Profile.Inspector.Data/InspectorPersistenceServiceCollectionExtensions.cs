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
        var connectionString = configuration.GetConnectionString("Inspector")
            ?? configuration.GetConnectionString("Core")
            ?? throw new InvalidOperationException(
                "Не задана строка подключения 'ConnectionStrings:Inspector' (или 'Core') для слоя данных профиля.");

        services.AddDbContextFactory<InspectorDbContext>(options =>
            options.UseNpgsql(connectionString, npg =>
                    npg.MigrationsHistoryTable("__ef_migrations_history", InspectorDbContext.Schema))
                .UseCamelCaseNamingConvention());

        return services;
    }
}

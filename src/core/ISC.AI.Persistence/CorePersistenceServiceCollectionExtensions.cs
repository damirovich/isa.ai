using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pgvector.EntityFrameworkCore;

namespace ISC.AI.Persistence;

/// <summary>Регистрация инфраструктуры данных ядра (схема <c>core</c>) в контейнере хоста.</summary>
public static class CorePersistenceServiceCollectionExtensions
{
    /// <summary>
    /// Регистрирует <see cref="CoreDbContext"/> через фабрику (ТС-008) с провайдером Npgsql.
    /// История миграций — в схеме <c>core</c>. Строка подключения — секция
    /// <c>ConnectionStrings:Core</c> конфигурации.
    /// </summary>
    /// <remarks>
    /// Регистрация не открывает соединение — хост стартует и без доступной БД; соединение
    /// устанавливается при первом обращении. Универсальный контекст ядра регистрирует хост;
    /// доменный контекст профиля (схема <c>inspector</c>) — сам профиль (ДОК-05, ТО-инф-01).
    /// </remarks>
    public static IServiceCollection AddCorePersistence(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Core")
            ?? throw new InvalidOperationException(
                "Не задана строка подключения 'ConnectionStrings:Core' для ядра данных.");

        services.AddDbContextFactory<CoreDbContext>(options =>
            options.UseNpgsql(connectionString, npg =>
                {
                    npg.MigrationsHistoryTable("__ef_migrations_history", CoreDbContext.Schema);
                    npg.UseVector(); // маппинг pgvector (ТО-инф-02)
                })
                .UseSnakeCaseNamingConvention());

        return services;
    }
}

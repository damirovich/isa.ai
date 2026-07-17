using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ISC.AI.Profile.ERP.Data;

/// <summary>Регистрация доменного слоя данных профиля «АИС ЕРП» (схема <c>erp</c>) в контейнере хоста.</summary>
public static class ErpPersistenceServiceCollectionExtensions
{
    /// <summary>
    /// Регистрирует <see cref="ErpDbContext"/> через фабрику (ТС-008) с провайдером Npgsql. История миграций —
    /// в схеме <c>erp</c>. Строка подключения — секция <c>ConnectionStrings:Erp</c> (при отсутствии — <c>Core</c>);
    /// пароль подставляется отдельным секретом <c>Database:Password</c> (Э4-10, секреты вне конфигов).
    /// </summary>
    public static IServiceCollection AddErpPersistence(
        this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = ConnectionStringResolver.Resolve(configuration, "Erp", fallbackName: "Core");

        services.AddDbContextFactory<ErpDbContext>(options =>
            options.UseNpgsql(connectionString, npg =>
                    npg.MigrationsHistoryTable("__ef_migrations_history", ErpDbContext.Schema))
                .UseSnakeCaseNamingConvention());

        return services;
    }
}

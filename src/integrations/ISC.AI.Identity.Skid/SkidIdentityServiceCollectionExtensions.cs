using ISC.AI.Abstractions.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ISC.AI.Identity.Skid;

/// <summary>Регистрация адаптера идентичности СКИД в контейнере хоста.</summary>
public static class SkidIdentityServiceCollectionExtensions
{
    /// <summary>
    /// Регистрирует read-only контекст БД СКИД и <see cref="IExternalIdentityProvider"/> поверх него.
    /// Строку подключения хост собирает сам (несекретная топология + отдельный секрет пароля
    /// <c>Database:Passwords:Skid</c>) — учётка должна быть read-only на уровне GRANT БД (ТД-004).
    /// </summary>
    public static IServiceCollection AddSkidIdentity(this IServiceCollection services, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.AddDbContextFactory<SkidDbContext>(options =>
            options.UseNpgsql(connectionString).UseSnakeCaseNamingConvention());

        services.AddScoped<IExternalIdentityProvider, SkidExternalIdentityProvider>();

        return services;
    }
}

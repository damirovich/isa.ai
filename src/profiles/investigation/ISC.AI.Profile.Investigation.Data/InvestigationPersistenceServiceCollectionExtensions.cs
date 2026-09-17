using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ISC.AI.Profile.Investigation.Data;

/// <summary>Регистрация слоя данных профиля «Следствие» (схема <c>investigation</c>) в контейнере хоста.</summary>
public static class InvestigationPersistenceServiceCollectionExtensions
{
    /// <summary>Регистрирует контекст данных и реализации портов профиля и пакетов. Заполняется на ЭС3-01.</summary>
    public static IServiceCollection AddInvestigationPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return services;
    }
}

using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ISC.AI.Modules.Media.Application;

/// <summary>Регистрация сценариев пакета «Медиа» (валидаторы, индексатор, настройки поиска) в контейнере хоста.</summary>
public static class MediaApplicationServiceCollectionExtensions
{
    /// <summary>Регистрирует валидаторы и прикладные службы пакета. Обработчики Mediator регистрирует хост.</summary>
    public static IServiceCollection AddMediaApplication(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddValidatorsFromAssembly(typeof(MediaApplicationServiceCollectionExtensions).Assembly);
        return services;
    }
}

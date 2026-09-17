using System;
using FluentValidation;
using ISC.AI.Modules.Media.Application.Features.Indexing;
using ISC.AI.Modules.Media.Domain.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ISC.AI.Modules.Media.Application;

/// <summary>Регистрация сценариев пакета «Медиа» (валидаторы, индексатор, настройки поиска) в контейнере хоста.</summary>
public static class MediaApplicationServiceCollectionExtensions
{
    /// <summary>
    /// Регистрирует валидаторы, настройки поиска (<see cref="MediaSearchOptions"/>, singleton) и конвейер
    /// индексации (<see cref="IMediaIndexer"/> → <see cref="MediaIndexer"/>, scoped — исполняется из
    /// per-operation scope фоновой очереди). Обработчики Mediator регистрирует хост (source-генератор).
    /// </summary>
    public static IServiceCollection AddMediaApplication(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddValidatorsFromAssembly(typeof(MediaApplicationServiceCollectionExtensions).Assembly);
        services.AddSingleton(MediaSearchOptions.Read(configuration));
        services.AddScoped<IMediaIndexer, MediaIndexer>();
        return services;
    }
}

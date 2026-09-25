using System;
using FluentValidation;
using ISC.AI.Modules.Media.Application.Features.Indexing;
using ISC.AI.Modules.Media.Application.Features.Maintenance;
using ISC.AI.Modules.Media.Application.Features.Transcription;
using ISC.AI.Modules.Media.Domain.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ISC.AI.Modules.Media.Application;

/// <summary>
/// Регистрация сценариев пакета «Медиа» (валидаторы, индексатор, конвейер расшифровки, настройки поиска) в
/// контейнере хоста.
/// </summary>
public static class MediaApplicationServiceCollectionExtensions
{
    /// <summary>
    /// Регистрирует валидаторы, настройки поиска (<see cref="MediaSearchOptions"/>, singleton), конвейер
    /// индексации (<see cref="IMediaIndexer"/> → <see cref="MediaIndexer"/>) и конвейер расшифровки речи
    /// (<see cref="IMediaTranscriptionPipeline"/> → <see cref="MediaTranscriptionPipeline"/>, ADR-0026) —
    /// оба scoped: исполняются из per-operation scope фоновой очереди. Их временные копии — в управляемом
    /// каталоге (<see cref="MediaTempFiles"/>, singleton), который вместе с восстановлением прерванных статусов
    /// обслуживает при старте хоста <see cref="MediaStartupMaintenance"/> (ТБ-064). Порт распознавателя
    /// (<see cref="IAudioTranscriber"/>) регистрирует интеграция <c>ISC.AI.Speech</c>. Обработчики Mediator
    /// регистрирует хост (source-генератор).
    /// </summary>
    public static IServiceCollection AddMediaApplication(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddValidatorsFromAssembly(typeof(MediaApplicationServiceCollectionExtensions).Assembly);
        services.AddSingleton(MediaSearchOptions.Read(configuration));
        services.AddSingleton(new MediaTempFiles());
        services.AddScoped<IMediaIndexer, MediaIndexer>();
        services.AddScoped<IMediaTranscriptionPipeline, MediaTranscriptionPipeline>();
        services.AddHostedService<MediaStartupMaintenance>();
        return services;
    }
}

using System;
using FluentValidation;
using ISC.AI.Modules.Media.Application.Features.Indexing;
using ISC.AI.Modules.Media.Application.Features.Maintenance;
using ISC.AI.Modules.Media.Application.Features.Transcription;
using ISC.AI.Modules.Media.Domain.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ISC.AI.Modules.Media.Application;

/// <summary>
/// Регистрация сценариев пакета «Медиа» (валидаторы, индексатор, конвейер расшифровки, настройки поиска) в
/// контейнере хоста.
/// </summary>
public static class MediaApplicationServiceCollectionExtensions
{
    /// <summary>
    /// Регистрирует валидаторы, настройки поиска (<see cref="MediaSearchOptions"/>, singleton), пределы приёма файлов
    /// (<see cref="MediaUploadOptions"/>, singleton, ADR-0040), конвейер
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
        services.AddSingleton(MediaUploadOptions.Read(configuration));
        services.AddSingleton(new MediaTempFiles());
        services.AddScoped<IMediaIndexer, MediaIndexer>();

        // ТФ-ПЕР-09: предложения связей с фигурантами после индексации нового носителя (ADR-0035).
        services.AddScoped<IPersonSuggester, Features.Suggestions.PersonSuggester>();
        services.AddScoped<IPersonSuggestionScheduler, Features.Suggestions.PersonSuggestionScheduler>();

        // ТФ-ПЛ-02: когда, откуда и где снят материал кандидата — для очереди, страницы решения и сессии поиска.
        services.AddScoped<Features.Verification.IMaterialContextReader, Features.Verification.MaterialContextReader>();
        services.AddScoped<IMediaTranscriptionPipeline, MediaTranscriptionPipeline>();

        // Правки дикторов расшифровки (ADR-0062): общая проверка права, решётки и дел субъекта.
        services.AddScoped<Features.Transcripts.TranscriptSpeakerGuard>();
        services.AddHostedService<MediaStartupMaintenance>();

        // Пакетный импорт архива с описью (ADR-0041): разрешённые каталоги и порции обработки — из настройки;
        // проверка описи, исполнитель строк и подача в обработку — scoped; фоновая служба стартует после хоста.
        services.AddSingleton(MediaImportOptions.Read(configuration));
        services.TryAddSingleton(TimeProvider.System);

        // Документы архива (ADR-0042) кладёт профиль со своим документооборотом; без него — явный отказ по строке.
        services.TryAddScoped<IArchiveDocumentSink, Features.Import.UnsupportedArchiveDocumentSink>();

        // Адреса карточек профиля для переходов со страниц пакета (ADR-0059); профиль без своих карточек — переходов нет.
        services.TryAddSingleton<IMediaLinks, Features.Navigation.NoMediaLinks>();
        services.AddScoped<Features.Import.MediaImportPlanner>();
        services.AddScoped<Features.Import.MediaImportRunner>();
        services.AddScoped<Features.Import.MediaImportFeeder>();
        services.AddHostedService<Features.Import.MediaImportWorker>();
        return services;
    }
}

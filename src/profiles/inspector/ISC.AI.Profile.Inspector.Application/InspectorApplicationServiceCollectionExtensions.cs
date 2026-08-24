using FluentValidation;
using ISC.AI.Abstractions.AI;
using ISC.AI.Abstractions.Grounding;
using ISC.AI.Abstractions.Ingestion;
using ISC.AI.Profile.Inspector.Application.Features.Generation;
using ISC.AI.Profile.Inspector.Application.Features.Grounding;
using ISC.AI.Profile.Inspector.Application.Features.Ingestion;
using ISC.AI.Profile.Inspector.Domain.Risk;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ISC.AI.Profile.Inspector.Application;

/// <summary>Регистрация сценариев профиля «Инспектор» (промпты, валидаторы, доменные швы грунтовки/чанкинга).
/// Обработчики Mediator регистрирует source-генератор в хосте <c>Web</c>.</summary>
public static class InspectorApplicationServiceCollectionExtensions
{
    /// <summary>Регистрирует промпт-рендереры, валидаторы и профильные реализации грунтовки/чанкинга НПА.</summary>
    public static IServiceCollection AddInspectorApplication(this IServiceCollection services)
    {
        // Провайдер задачных промптов (ТО-прог-04): грузит файлы-шаблоны .scriban ПО КЛЮЧУ из этой сборки.
        // Рендерер справки берёт шаблон через него, а не прямым чтением ресурса (ТО-лнг-03).
        services.AddSingleton<IPromptProvider>(
            new EmbeddedScribanPromptProvider(typeof(InspectorApplicationServiceCollectionExtensions).Assembly));
        services.AddSingleton<IReferencePromptRenderer, ScribanReferencePromptRenderer>();

        // Методики проверок (ТФ-МЕТ-01): второй генерирующий сценарий на том же конвейере —
        // свой задачный промпт (method.scriban), ядро и грунтовка не трогаются.
        services.AddSingleton<Features.Methods.IMethodPromptRenderer, Features.Methods.ScribanMethodPromptRenderer>();

        // Детерминированный расчёт риска подразделения (Э5-01 шаг 2, Приложение §2) — код, не ИИ.
        services.AddSingleton<RiskScoreCalculator>();

        // Профиль ПЕРЕОПРЕДЕЛЯЕТ доменные швы грунтовки/чанкинга НПА поверх нейтральных заглушек ядра
        // (Э4-18, инж-ТЗ §5.3.1.2/§5.3.1.3). Replace — явная замена дефолта, без «мёртвой» второй регистрации.
        // Инвариант грунтовки остаётся в ядре (ТБ-041): профиль поставляет извлечение/нормализацию/чанкинг,
        // но НЕ сам валидатор. Вызывается ПОСЛЕ AddCoreGrounding/AddCoreIngestion (точка композиции хоста).
        services.Replace(ServiceDescriptor.Singleton<ICitationExtractor, NpaCitationExtractor>());
        services.Replace(ServiceDescriptor.Singleton<ICitationNormalizer, NpaCitationNormalizer>());
        services.Replace(ServiceDescriptor.Singleton<ITextChunker, NpaStructuralChunker>());

        // Авто-регистрация всех IValidator<T> из этой сборки: новые валидаторы подхватываются без правок DI.
        services.AddValidatorsFromAssembly(typeof(InspectorApplicationServiceCollectionExtensions).Assembly);
        return services;
    }
}

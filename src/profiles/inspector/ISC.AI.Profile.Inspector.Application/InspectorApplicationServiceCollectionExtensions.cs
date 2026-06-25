using FluentValidation;
using ISC.AI.Profile.Inspector.Application.Generation;
using Microsoft.Extensions.DependencyInjection;

namespace ISC.AI.Profile.Inspector.Application;

/// <summary>Регистрация сценариев профиля «Инспектор» (промпты, валидаторы). Обработчики Mediator
/// регистрирует source-генератор в хосте <c>Web</c>.</summary>
public static class InspectorApplicationServiceCollectionExtensions
{
    /// <summary>Регистрирует промпт-рендереры и ВСЕ валидаторы профиля (сканированием сборки — не по одному).</summary>
    public static IServiceCollection AddInspectorApplication(this IServiceCollection services)
    {
        services.AddSingleton<IReferencePromptRenderer, ScribanReferencePromptRenderer>();

        // Авто-регистрация всех IValidator<T> из этой сборки: новые валидаторы подхватываются без правок DI.
        services.AddValidatorsFromAssembly(typeof(InspectorApplicationServiceCollectionExtensions).Assembly);
        return services;
    }
}

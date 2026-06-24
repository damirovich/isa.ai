using ISC.AI.Abstractions.Grounding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ISC.AI.AI.Grounding;

/// <summary>Регистрация грунтовки ядра (ТБ-040) в контейнере хоста.</summary>
public static class CoreGroundingServiceCollectionExtensions
{
    /// <summary>
    /// Регистрирует валидатор грунтовки и нейтральные дефолты извлечения/нормализации ссылок.
    /// Профиль может переопределить <see cref="ICitationExtractor"/>/<see cref="ICitationNormalizer"/>
    /// (НПА-паттерны), но не сам инвариант грунтовки (ТБ-041, ADR-0005).
    /// </summary>
    public static IServiceCollection AddCoreGrounding(this IServiceCollection services)
    {
        services.TryAddSingleton<ICitationNormalizer, IdentityCitationNormalizer>();
        services.TryAddSingleton<ICitationExtractor, NoCitationExtractor>();
        services.AddSingleton<IGroundingValidator, GroundingValidator>();
        return services;
    }
}

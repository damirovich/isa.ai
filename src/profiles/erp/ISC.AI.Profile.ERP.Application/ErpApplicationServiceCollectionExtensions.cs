using Microsoft.Extensions.DependencyInjection;

namespace ISC.AI.Profile.ERP.Application;

/// <summary>
/// Регистрация сценариев профиля «АИС ЕРП» (промпты, валидаторы, доменные швы). КАРКАС — состав
/// определится по деталям проекта и контракту API АИС ЕРП.
/// </summary>
/// <remarks>
/// Обработчики Mediator регистрирует source-генератор в хосте. Здесь — профильные сервисы: промпт-рендереры,
/// валидаторы, а также доменные переопределения ядровых швов (напр. извлечение текста рукописных сканов,
/// политика доступа <c>IAccessPolicy</c>) — по мере появления.
/// </remarks>
public static class ErpApplicationServiceCollectionExtensions
{
    /// <summary>Регистрирует сценарии профиля ЕРП. Пока пусто — каркас.</summary>
    public static IServiceCollection AddErpApplication(this IServiceCollection services) => services;
}

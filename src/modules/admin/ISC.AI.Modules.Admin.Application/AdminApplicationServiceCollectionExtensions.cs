using FluentValidation;
using ISC.AI.Modules.Admin.Application.Features.Clearances;
using Microsoft.Extensions.DependencyInjection;

namespace ISC.AI.Modules.Admin.Application;

/// <summary>
/// Регистрация прикладных сервисов пакета «Администрирование платформы» (валидаторы сценариев,
/// стартовая сверка допусков). Обработчики Mediator регистрирует source-генератор в хосте <c>Web</c>.
/// </summary>
public static class AdminApplicationServiceCollectionExtensions
{
    /// <summary>
    /// Регистрирует валидаторы сценариев пакета (сквозной <c>ValidationBehavior</c> хоста берёт их
    /// из DI) и разовую сверку допусков со справочником подразделений профиля.
    /// </summary>
    /// <remarks>
    /// Порты <c>IPlatformAdministration</c>, <c>IDivisionCatalog</c> и <c>IUserRoleCatalog</c> здесь
    /// НЕ регистрируются намеренно: их поставляет профиль. Без них контейнер не соберёт обработчик
    /// и приложение не запустится — это осознанный fail-closed (ТС-013): молчаливая заглушка вместо
    /// правила о доступе опаснее остановки.
    /// </remarks>
    public static IServiceCollection AddAdminApplication(this IServiceCollection services) =>
        services
            .AddValidatorsFromAssembly(typeof(AdminApplicationServiceCollectionExtensions).Assembly)
            .AddHostedService<ClearanceDivisionConsistencyCheck>();
}

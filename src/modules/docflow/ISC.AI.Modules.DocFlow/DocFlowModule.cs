using ISC.AI.Abstractions.Modules;
using ISC.AI.Modules.DocFlow.Application;
using ISC.AI.Modules.DocFlow.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ISC.AI.Modules.DocFlow;

/// <summary>
/// Манифест пакета модулей «Документооборот» (ADR-0017) — переиспользуемая вертикаль, которую ПРОФИЛЬ
/// включает в свой реестр модулей (<c>IProfile.Modules</c>).
/// </summary>
/// <remarks>
/// Это НЕ профиль: профиль в развёртывании ровно один (<c>IProfile</c>, ТС-004/006), а документооборот
/// нужен и «ИнспекторAI», и другим профилям (напр. «АИС ЕРП»), поэтому оформлен отдельным пакетом.
/// ИНВАРИАНТ: пакет НЕ зависит ни от одного профиля и ни от хоста — иначе переиспользование теряется
/// (проверяется архитектурным тестом <c>DependencyRulesTests</c>).
/// Порядок вызова со стороны профиля повторяет порядок композиции хоста (ТО-прог-05):
/// <see cref="RegisterServices"/> → <see cref="RegisterDataContexts"/>.
/// </remarks>
public static class DocFlowModule
{
    /// <summary>Секция меню, под которой профиль группирует страницы документооборота.</summary>
    public const string MenuGroup = "Документооборот";

    /// <summary>
    /// Реестр страниц модуля — профиль подмешивает его в свой <c>IProfile.Modules</c>.
    /// СКЕЛЕТ (этап 0 задачи Э4-35): пуст, дескрипторы появляются вместе со страницами (этапы 1 и 5).
    /// </summary>
    public static IReadOnlyList<IModule> Modules { get; } = [];

    /// <summary>Прикладные сервисы модуля — вызывается профилем в <c>IProfile.RegisterServices</c>.</summary>
    public static IServiceCollection RegisterServices(IServiceCollection services) =>
        services.AddDocFlowApplication();

    /// <summary>Контексты данных модуля — вызывается профилем в <c>IProfile.RegisterDataContexts</c>.</summary>
    public static IServiceCollection RegisterDataContexts(
        IServiceCollection services, IConfiguration configuration) =>
        services.AddDocFlowPersistence(configuration);
}

using ISC.AI.Abstractions.AI;
using ISC.AI.Abstractions.Modules;
using ISC.AI.Abstractions.Profiles;
using ISC.AI.Profile.Inspector.Application;
using ISC.AI.Profile.Inspector.Data;
using ISC.AI.Profile.Inspector.UI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ISC.AI.Profile.Inspector;

/// <summary>
/// Манифест профиля «ИнспекторAI» — точка декларативного описания вертикали инспекции.
/// Подключается единственным профилем в хосте <c>ISC.AI.Web</c> (ТС-004, ТС-006).
/// </summary>
/// <remarks>
/// На текущем этапе (Э2 — каркас композиции) объявлен один модуль-заглушка («Дашборд»),
/// чтобы проверить сквозную сборку «реестр модулей → навигация хоста → страница из RCL».
/// Регистрация сервисов, контекстов данных и привязок моделей появляется на этапах Э3+.
/// </remarks>
public sealed class InspectorProfile : IProfile
{
    /// <inheritdoc />
    public string Id => "inspector";

    /// <inheritdoc />
    public string DisplayName => "ИнспекторAI";

    /// <inheritdoc />
    public IReadOnlyList<IModule> Modules { get; } =
    [
        new ModuleDescriptor(
            Id: "dashboard",
            Route: "/dashboard",
            MenuTitle: "Дашборд",
            MenuIcon: null,
            ComponentType: typeof(Dashboard),
            RequiredPolicy: "inspector.read"),
        new ModuleDescriptor(
            Id: "generator",
            Route: "/generator",
            MenuTitle: "Генератор",
            MenuIcon: null,
            ComponentType: typeof(Generator),
            RequiredPolicy: "inspector.read"),
    ];

    /// <inheritdoc />
    public IReadOnlyList<IModelContributor> ModelContributors { get; } = [];

    /// <inheritdoc />
    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        // Сценарии профиля: промпт-рендереры и валидаторы (Mediator-обработчики регистрирует
        // source-генератор в хосте Web). Сейчас — сценарий «Генератор» (Э4-03).
        services.AddInspectorApplication();
    }

    /// <inheritdoc />
    public void RegisterDataContexts(IServiceCollection services, IConfiguration configuration)
    {
        // Доменный контекст профиля (схема inspector) через фабрику (ТС-008, ТО-инф-01, ADR-0003).
        services.AddInspectorPersistence(configuration);
    }
}

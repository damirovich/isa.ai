using ISC.AI.Abstractions.Modules;
using ISC.AI.Modules.DocFlow.Application;
using ISC.AI.Modules.DocFlow.Data;
using ISC.AI.Modules.DocFlow.UI;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;

namespace ISC.AI.Modules.DocFlow;

/// <summary>
/// Манифест пакета модулей «Документооборот» (ADR-0017) — переиспользуемая вертикаль, которую ПРОФИЛЬ
/// включает в свой реестр модулей (<c>IProfile.Modules</c>).
/// </summary>
/// <remarks>
/// Это НЕ профиль: профиль в развёртывании ровно один (<c>IProfile</c>, ТС-004/006), а документооборот
/// нужен и «ИнспекторAI», и любому будущему профилю-эксплуатанту, поэтому оформлен отдельным пакетом.
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
    /// Политика доступа страниц модуля. Пока — «аутентифицирован» (регистрируется хостом из реестра);
    /// ролевое разграничение по ТЗ СКИД §2.1 (справочники — Администратор, построчные правила — через
    /// <c>IAccessPolicy</c>) — этап 6 Э4-35.
    /// </summary>
    public const string ReadPolicy = "docflow.read";

    /// <summary>
    /// Реестр страниц модуля — профиль подмешивает его в свой <c>IProfile.Modules</c>.
    /// Этап 1 Э4-35: справочник типов; остальные страницы СКИД — этап 5.
    /// </summary>
    public static IReadOnlyList<IModule> Modules { get; } =
    [
        new ModuleDescriptor("docflow-documents", "/docflow/documents", "Документы",
            Icons.Material.Filled.Description, typeof(Documents), ReadPolicy, MenuGroup),
        new ModuleDescriptor("docflow-reports", "/docflow/reports", "Отчёты",
            Icons.Material.Filled.Assessment, typeof(Reports), ReadPolicy, MenuGroup),
        new ModuleDescriptor("docflow-types", "/docflow/types", "Типы документов",
            Icons.Material.Filled.Category, typeof(DocumentTypes), ReadPolicy, MenuGroup),
    ];

    /// <summary>
    /// Виджеты оболочки — профиль подмешивает их в свой <c>IProfile.ShellWidgets</c>: счётчик
    /// уведомлений в шапке и лента уведомлений на общем дашборде (разд. 5 ТЗ СКИД).
    /// </summary>
    /// <remarks>
    /// Дашборд в системе ОДИН и живёт ОТДЕЛЬНЫМ экраном хоста (<c>/dashboard</c>, решение заказчика
    /// 2026-08-07): своей страницы документооборот не заводит, а добавляет туда панель через слот
    /// <see cref="ShellWidgetSlot.Dashboard"/>. Колокольчик в шапке — только счётчик, ведущий туда же.
    /// </remarks>
    public static IReadOnlyList<IShellWidget> ShellWidgets { get; } =
    [
        new ShellWidgetDescriptor(
            "docflow-notifications", ShellWidgetSlot.AppBarRight, Order: 10, typeof(NotificationBell)),

        // Columns: 12 — лента занимает весь ряд. Пока это единственная панель дашборда; когда рядом
        // появятся показатели, ширину здесь и уменьшим (хост раскладывает по числу, а не по смыслу).
        new ShellWidgetDescriptor(
            "docflow-notifications-panel", ShellWidgetSlot.Dashboard, Order: 10,
            typeof(NotificationsPanel), Columns: 12),
    ];

    /// <summary>Прикладные сервисы модуля — вызывается профилем в <c>IProfile.RegisterServices</c>.</summary>
    public static IServiceCollection RegisterServices(IServiceCollection services) =>
        services.AddDocFlowApplication();

    /// <summary>Контексты данных модуля — вызывается профилем в <c>IProfile.RegisterDataContexts</c>.</summary>
    public static IServiceCollection RegisterDataContexts(
        IServiceCollection services, IConfiguration configuration) =>
        services.AddDocFlowPersistence(configuration);

    /// <summary>
    /// Сырые HTTP-эндпоинты модуля (этап 4.3 Э4-35): раздача файлов (§3.3), не Blazor-страницы —
    /// вызывается профилем после построения приложения хостом (композиция, не DI-этап).
    /// </summary>
    public static IEndpointRouteBuilder MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapDocFlowFileEndpoints();
        return endpoints;
    }
}

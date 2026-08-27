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
    /// ЧТО ПРОФИЛЬ ОБЯЗАН ПРЕДОСТАВИТЬ, подключая этот пакет.
    /// </summary>
    /// <remarks>
    /// Это ВЕСЬ внешний контракт модуля — три порта, которые он объявляет, но реализовать не может,
    /// потому что ответы на них знает только эксплуатант:
    /// <list type="bullet">
    /// <item><c>IDivisionDirectory</c> — справочник подразделений (словарь идентификаторов един
    /// с решёткой доступа ядра; свой справочник модуль не заводит, вопрос 3 Э4-35);</item>
    /// <item><c>IDocFlowAdministration</c> — кто вправе вести настройки и справочники модуля
    /// (право определяется РОЛЬЮ, а роли ведёт профиль);</item>
    /// <item><c>IAssignmentCandidateDirectory</c> — кого предлагать в инспекторы и исполнители
    /// (тоже роль плюс допуск).</item>
    /// </list>
    /// Забыл профиль любой из них — приложение НЕ ЗАПУСТИТСЯ: контейнер не соберёт обработчик.
    /// Это намеренно: молчаливая заглушка вместо правила о доступе опаснее остановки, а разбираться
    /// с ней пришлось бы уже по следам чужих действий.
    ///
    /// Сверх этого модуль пользуется НЕЙТРАЛЬНЫМИ службами ядра, которые даёт хост:
    /// <c>IAccessPolicy</c>, <c>IAccessContextProvider</c>, <c>ISubjectProvider</c>,
    /// <c>IAuditWriter</c>, <c>IBackgroundTaskQueue</c>.
    ///
    /// Список закреплён тестом <c>DocFlowContractTests</c>: он не должен разрастаться незаметно —
    /// каждый новый пункт удорожает подключение модуля к следующему профилю.
    /// </remarks>
    public static IReadOnlyList<Type> RequiredServices { get; } =
    [
        typeof(Domain.Services.IDivisionDirectory),
        typeof(Domain.Services.IDocFlowAdministration),
        typeof(Domain.Services.IAssignmentCandidateDirectory),
    ];

    /// <summary>
    /// Ключи конфигурации, которые читает модуль (все — необязательные, у каждого есть значение
    /// по умолчанию, кроме строки подключения).
    /// </summary>
    /// <remarks>
    /// Перечислены здесь, чтобы при подключении к новому профилю их не искали по коду:
    /// <c>ConnectionStrings:DocFlow</c> (при отсутствии — <c>ConnectionStrings:Core</c>),
    /// <c>DocFlow:Storage:BasePath</c>, <c>DocFlow:TimeZone</c>,
    /// <c>DocFlow:DeadlineCheckIntervalMinutes</c>, <c>DocFlow:NotificationHorizonDays</c>
    /// (запасной вариант для системной настройки §9), <c>DocFlow:Reports:PdfFont:Regular</c> и
    /// <c>:Bold</c>.
    /// </remarks>
    public static IReadOnlyList<string> ConfigurationKeys { get; } =
    [
        "ConnectionStrings:DocFlow",
        "DocFlow:Storage:BasePath",
        "DocFlow:TimeZone",
        "DocFlow:DeadlineCheckIntervalMinutes",
        "DocFlow:NotificationHorizonDays",
        "DocFlow:Reports:PdfFont:Regular",
        "DocFlow:Reports:PdfFont:Bold",
    ];

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
    /// <remarks>
    /// Отдельной страницы «Дашборд» у модуля больше НЕТ (объединение дашбордов, решение заказчика
    /// 2026-08-27): сводка отдана компонентом <c>DocFlowSummary</c>, и ПРОФИЛЬ сам решает, где её
    /// показать (у «ИнспекторAI» — вкладка общего дашборда <c>/dashboard</c>). Модульный маршрут
    /// <c>/docflow/dashboard</c> продолжает работать: профиль держит на нём страницу-перенаправление —
    /// модуль маршрутов профиля не знает (инвариант ADR-0017).
    /// </remarks>
    public static IReadOnlyList<IModule> Modules { get; } =
    [
        new ModuleDescriptor("docflow-documents", "/docflow/documents", "Документы",
            Icons.Material.Filled.Description, typeof(Documents), ReadPolicy, MenuGroup),
        new ModuleDescriptor("docflow-reports", "/docflow/reports", "Отчёты",
            Icons.Material.Filled.Assessment, typeof(Reports), ReadPolicy, MenuGroup),
        new ModuleDescriptor("docflow-types", "/docflow/types", "Типы документов",
            Icons.Material.Filled.Category, typeof(DocumentTypes), ReadPolicy, MenuGroup),
        new ModuleDescriptor("docflow-settings", "/docflow/settings", "Настройки",
            Icons.Material.Filled.Tune, typeof(DocFlowSettingsPage), ReadPolicy, MenuGroup),
    ];

    /// <summary>
    /// Виджеты оболочки — профиль подмешивает их в свой <c>IProfile.ShellWidgets</c>: счётчик
    /// уведомлений в шапке (разд. 5 ТЗ СКИД).
    /// </summary>
    /// <remarks>
    /// Лента уведомлений живёт в сводке модуля (<c>DocFlowSummary</c>). Колокольчик в шапке ведёт на
    /// СВОЙ маршрут модуля <c>/docflow/dashboard</c> — куда тот приземляется, решает профиль
    /// (страница-перенаправление на вкладку общего дашборда): модуль не знает маршрутов профиля.
    /// </remarks>
    public static IReadOnlyList<IShellWidget> ShellWidgets { get; } =
    [
        new ShellWidgetDescriptor(
            "docflow-notifications", ShellWidgetSlot.AppBarRight, Order: 10, typeof(NotificationBell)),
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

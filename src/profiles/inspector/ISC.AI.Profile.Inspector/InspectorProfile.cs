using ISC.AI.Abstractions.AI;
using ISC.AI.Abstractions.Modules;
using ISC.AI.Abstractions.Profiles;
using ISC.AI.Modules.DocFlow;
using ISC.AI.Profile.Inspector.Application;
using ISC.AI.Profile.Inspector.Data;
using ISC.AI.Profile.Inspector.UI;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;

namespace ISC.AI.Profile.Inspector;

/// <summary>
/// Манифест профиля «ИнспекторAI» — точка декларативного описания вертикали инспекции.
/// Подключается единственным профилем в хосте <c>ISC.AI.Web</c> (ТС-004, ТС-006).
/// </summary>
/// <remarks>
/// Реестр модулей соответствует набору из §5.2 ТЗ (11 модулей) плюс операционная «Загрузка корпуса».
/// Реализованы: Дашборд, Генератор, База НПА, Загрузка; остальные — страницы-заглушки «в разработке»
/// (очередь Ф1/Ф2 по §6.2). Секции меню объявляет профиль (<c>MenuGroup</c>) — хост лишь группирует.
/// </remarks>
public sealed class InspectorProfile : IProfile
{
    private const string GroupMain = "Навигация";
    private const string GroupDivisions = "Подразделения";
    private const string GroupAdmin = "Администрирование";
    private const string ReadPolicy = "inspector.read";

    /// <inheritdoc />
    public string Id => "inspector";

    /// <inheritdoc />
    public string DisplayName => "ИнспекторAI";

    /// <inheritdoc />
    public string? Subtitle => "ГКНБ КР · Главная инспекция";

    /// <inheritdoc />
    public IReadOnlyList<IModule> Modules { get; } =
    [
        // --- Реализованные модули ---
        new ModuleDescriptor("dashboard", "/dashboard", "Дашборд", Icons.Material.Filled.Dashboard,
            typeof(Dashboard), ReadPolicy, GroupMain),
        new ModuleDescriptor("generator", "/generator", "Генератор", Icons.Material.Filled.AutoAwesome,
            typeof(Generator), ReadPolicy, GroupMain),
        new ModuleDescriptor("chat", "/chat", "Чат-ассистент", Icons.Material.Filled.Forum,
            typeof(ChatAssistant), ReadPolicy, GroupMain),
        new ModuleDescriptor("npa-search", "/npa", "База НПА", Icons.Material.Filled.Gavel,
            typeof(NpaSearch), ReadPolicy, GroupMain),

        // --- Модули §5.2, ещё не реализованные (страницы-заглушки) ---
        new ModuleDescriptor("archive", "/archive", "Архив", Icons.Material.Filled.Inventory2,
            typeof(Archive), ReadPolicy, GroupMain),
        new ModuleDescriptor("collegium", "/collegium", "Коллегия", Icons.Material.Filled.Groups,
            typeof(Collegium), ReadPolicy, GroupMain),
        new ModuleDescriptor("meetings", "/meetings", "Совещания", Icons.Material.Filled.EventNote,
            typeof(Meetings), ReadPolicy, GroupMain),
        new ModuleDescriptor("editor", "/editor", "Редактор", Icons.Material.Filled.EditNote,
            typeof(Editor), ReadPolicy, GroupMain),
        new ModuleDescriptor("analysis", "/analysis", "Анализ / Сравнение", Icons.Material.Filled.CompareArrows,
            typeof(Analysis), ReadPolicy, GroupMain),
        new ModuleDescriptor("risks", "/risks", "Риски и контроль", Icons.Material.Filled.Warning,
            typeof(Risks), ReadPolicy, GroupMain),
        new ModuleDescriptor("methods", "/methods", "Методики проверок", Icons.Material.Filled.MenuBook,
            typeof(Methods), ReadPolicy, GroupMain),
        new ModuleDescriptor("monitoring", "/monitoring", "Мониторинг", Icons.Material.Filled.MonitorHeart,
            typeof(Monitoring), ReadPolicy, GroupMain),

        // --- Операционный модуль (вне §5.2): наполнение корпуса ---
        new ModuleDescriptor("load", "/load", "Загрузка корпуса", Icons.Material.Filled.CloudUpload,
            typeof(CorpusLoad), ReadPolicy, GroupMain),

        // --- Секция «Подразделения» (объекты контроля, §4.2) ---
        new ModuleDescriptor("divisions-territorial", "/divisions/territorial", "Территориальные",
            Icons.Material.Filled.AccountTree, typeof(TerritorialDivisions), ReadPolicy, GroupDivisions),
        new ModuleDescriptor("divisions-linear", "/divisions/linear", "Линейные",
            Icons.Material.Filled.Business, typeof(LinearDivisions), ReadPolicy, GroupDivisions),

        // --- Секция «Администрирование»: роли (§2.1 ТЗ СКИД, этап 6 Э4-35) — страница видна всем
        // (в claim'ах сессии нет роли, см. RoleScenarios.cs), обработчики отклоняют вызывающего,
        // который сам не Администратор.
        new ModuleDescriptor("admin-roles", "/admin/roles", "Роли пользователей",
            Icons.Material.Filled.AdminPanelSettings, typeof(UserRoles), ReadPolicy, GroupAdmin),

        // --- Секция «Документооборот»: подключаемый пакет модулей docflow (ADR-0017, Э4-35) ---
        // Страницы объявляет САМ модуль; профиль лишь включает их в свой реестр. Пока пусто (скелет, этап 0).
        .. DocFlowModule.Modules,
    ];

    /// <inheritdoc />
    /// <remarks>Колокольчик уведомлений даёт пакет docflow — профиль лишь включает его в оболочку.</remarks>
    public IReadOnlyList<IShellWidget> ShellWidgets { get; } = [.. DocFlowModule.ShellWidgets];

    /// <inheritdoc />
    public IReadOnlyList<IModelContributor> ModelContributors { get; } = [];

    /// <inheritdoc />
    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        // Сценарии профиля: промпт-рендереры, валидаторы, доменные швы грунтовки/чанкинга, расчёт риска.
        services.AddInspectorApplication();

        // Прикладные сервисы подключённых пакетов модулей (ADR-0017).
        DocFlowModule.RegisterServices(services);
    }

    /// <inheritdoc />
    public void RegisterDataContexts(IServiceCollection services, IConfiguration configuration)
    {
        // Доменный контекст профиля (схема inspector) через фабрику (ТС-008, ТО-инф-01, ADR-0003).
        services.AddInspectorPersistence(configuration);

        // Контексты данных подключённых пакетов модулей — своя схема и своя история миграций (ADR-0017).
        DocFlowModule.RegisterDataContexts(services, configuration);
    }

    /// <summary>
    /// Сырые HTTP-эндпоинты профиля (этап 4.3 Э4-35) — вызывается ХОСТОМ напрямую на конкретном типе
    /// (не через <see cref="IProfile"/>): композиция уже знает конкретный профиль (ADR-0002), лишний
    /// метод в тонком контракте ядра не нужен. Делегирует подключённым пакетам модулей.
    /// </summary>
    public void MapEndpoints(IEndpointRouteBuilder endpoints) => DocFlowModule.MapEndpoints(endpoints);
}

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
        // Картотека НПА (ТФ-НПА-02): ведение норм/редакций и связок с корпусом — то, что делает
        // смену статуса редакции действенной (GATE-3). Маршрут /norms, НЕ /npa/*: NavMenu подсвечивает
        // пункты по префиксу, и вложенный маршрут подсвечивал бы «Базу НПА» вместе с картотекой.
        new ModuleDescriptor("npa-registry", "/norms", "Картотека НПА", Icons.Material.Filled.LibraryBooks,
            typeof(NpaRegistry), ReadPolicy, GroupMain),
        // Учёт нарушений (Э5-01, Приложение §4): реестр фактов, которыми живут светофор риска и дашборд.
        new ModuleDescriptor("violations", "/violations", "Учёт нарушений", Icons.Material.Filled.ReportProblem,
            typeof(Violations), ReadPolicy, GroupMain),
        // Архив проверок (§5.2.4, ТФ-АРХ-01/02): история нарушений по справкам-проверкам и
        // подразделениям; метаданные справок — из документооборота через решётку доступа.
        new ModuleDescriptor("archive", "/archive", "Архив", Icons.Material.Filled.Inventory2,
            typeof(Archive), ReadPolicy, GroupMain),
        // Редактор (§5.2.10, ТФ-РЕД-01..03): ИИ-правка по команде с сохранением грунтовки.
        new ModuleDescriptor("editor", "/editor", "Редактор", Icons.Material.Filled.EditNote,
            typeof(Editor), ReadPolicy, GroupMain),
        // Анализ / Сравнение (§5.2.3, ТФ-НПА-03/04): противоречия и пробелы между НПА, анализ
        // документа — вдумчивая роль Analysis (размышления по-ролево, ADR-0011). Последний P1.
        new ModuleDescriptor("analysis", "/analysis", "Анализ / Сравнение", Icons.Material.Filled.CompareArrows,
            typeof(Analysis), ReadPolicy, GroupMain),
        new ModuleDescriptor("risks", "/risks", "Риски и контроль", Icons.Material.Filled.Warning,
            typeof(Risks), ReadPolicy, GroupMain),
        new ModuleDescriptor("methods", "/methods", "Методики проверок", Icons.Material.Filled.MenuBook,
            typeof(Methods), ReadPolicy, GroupMain),
        new ModuleDescriptor("monitoring", "/monitoring", "Мониторинг", Icons.Material.Filled.MonitorHeart,
            typeof(Monitoring), ReadPolicy, GroupMain),
        // Совещания (§5.2.7, ТФ-СОВ-01/02): протоколы — документы группы «Исполнение» документооборота
        // (пункты = назначения, контроль сроков — там); модуль добавляет реестр протоколов и справку
        // об исполнении (факты считает код, ИИ ролью Draft пишет только текст).
        new ModuleDescriptor("meetings", "/meetings", "Совещания", Icons.Material.Filled.EventNote,
            typeof(Meetings), ReadPolicy, GroupMain),

        // --- Модули §5.2, ещё не реализованные (страницы-заглушки, волна P2) ---
        new ModuleDescriptor("collegium", "/collegium", "Коллегия", Icons.Material.Filled.Groups,
            typeof(Collegium), ReadPolicy, GroupMain),

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

        // Допуски (ТБ-011/020/021) — гриф и подразделения. Раньше правились только SQL'ем по живой
        // базе, мимо неизменяемого журнала; страница закрывает это и показывает расхождение со
        // справочником подразделений.
        new ModuleDescriptor("admin-clearances", "/admin/clearances", "Допуски пользователей",
            Icons.Material.Filled.Key, typeof(UserClearances), ReadPolicy, GroupAdmin),

        // Учётные записи (Э4-35 §6.5): после перехода на локальную идентичность — единственное
        // место, где заводят доступ и восстанавливают забытый пароль.
        new ModuleDescriptor("admin-users", "/admin/users", "Учётные записи",
            Icons.Material.Filled.ManageAccounts, typeof(UserAccounts), ReadPolicy, GroupAdmin),

        // Журнал аудита (ТБ-030/032): писался с самого начала, но смотреть его из интерфейса было
        // нельзя — только запросом к БД.
        new ModuleDescriptor("admin-audit", "/admin/audit", "Журнал аудита",
            Icons.Material.Filled.History, typeof(AuditJournal), ReadPolicy, GroupAdmin),

        // Классификатор видов нарушений (Э5-01): справочник «сфера → вид», ведёт Администратор.
        new ModuleDescriptor("admin-violation-categories", "/admin/violation-categories", "Виды нарушений",
            Icons.Material.Filled.Category, typeof(ViolationCategories), ReadPolicy, GroupAdmin),

        // Смена СВОЕГО пароля — не администрирование, доступна любому вошедшему.
        new ModuleDescriptor("account-password", "/account/password", "Смена пароля",
            Icons.Material.Filled.Password, typeof(ChangePassword), ReadPolicy, GroupAdmin),

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

        // Передача черновика Генератор → Редактор без копирования руками (scoped = одна сессия).
        services.AddScoped<EditorDraftHandoff>();

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

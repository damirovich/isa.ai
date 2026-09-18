using ISC.AI.Abstractions.AI;
using ISC.AI.Abstractions.Modules;
using ISC.AI.Abstractions.Profiles;
using ISC.AI.Modules.Admin;
using ISC.AI.Modules.DocFlow;
using ISC.AI.Modules.Media;
using ISC.AI.Profile.Investigation.Application;
using ISC.AI.Profile.Investigation.Data;
using ISC.AI.Profile.Investigation.UI;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;

namespace ISC.AI.Profile.Investigation;

/// <summary>
/// Манифест профиля «СледствиеAI» (ADR-0021, ДОК-13 §6) — декларативное описание вертикали
/// следственного подразделения: дела и фигуранты (свои страницы), пакет «Медиа» (носители, поиск по
/// лицу, верификация — ADR-0022), пакет «Документооборот» («Документы дела») и пакет
/// «Администрирование платформы» (учётные записи, допуски, журнал аудита — ADR-0023).
/// Подключается единственным профилем в хосте <c>ISC.AI.Web</c> при <c>IscProfile=investigation</c>
/// (ТС-004, ТС-006).
/// </summary>
/// <remarks>
/// <para>
/// ПОРТЫ ПАКЕТОВ, которые профиль обязан закрыть: <see cref="DocFlowModule.RequiredServices"/> (справочник
/// подразделений, право настройки, кандидаты в исполнители), <see cref="MediaModule.RequiredServices"/>
/// (область дел субъекта, права по ролям, роли стадий верификации) и <see cref="AdminModule.RequiredServices"/>
/// (право вести учётные записи и допуски, право читать журнал, справочник подразделений, роли для
/// показа). Все реализации — в
/// <c>Investigation.Data</c> (<c>AddInvestigationPersistence</c>). Отсутствие любого порта профиль
/// обнаруживает САМ в конце <see cref="RegisterDataContexts"/> (<see cref="EnsureModulePortsRegistered"/>)
/// и бросает <see cref="InvalidOperationException"/> с именем порта — приложение не стартует в ЛЮБОМ
/// окружении, а не только в Development, где контейнер ASP.NET валидирует граф при сборке. Факт
/// регистрации закреплён тестом <c>InvestigationProfileTests</c>.
/// </para>
/// <para>
/// Секции меню (порядок первого появления): «Дела» → «Медиа» (страницы пакета) → «Документооборот»
/// (страницы пакета) → «Администрирование» (страницы пакета «Администрирование» и две страницы
/// профиля — роли и подразделения). Страницы <c>/account/password</c> (принудительная смена временного
/// пароля — <c>RequirePasswordChange</c> хоста, сборка <c>Modules.Admin.UI</c>) и <c>/docflow/dashboard</c>
/// (адрес колокольчика уведомлений пакета docflow) в реестре не значатся, но существуют в сборках UI,
/// которые попадают в маршрутизацию через записи реестра ниже.
/// </para>
/// </remarks>
public sealed class InvestigationProfile : IProfile
{
    private const string GroupCases = "Дела";

    // Секция меню администрирования — ОБЩАЯ с пакетом: страницы пакета и страницы профиля стоят в
    // одном разделе, иначе в меню было бы два «Администрирования» подряд.
    private const string GroupAdmin = AdminModule.MenuGroup;

    /// <summary>Политика доступа страниц профиля («аутентифицирован» — регистрируется хостом из реестра).</summary>
    public const string ReadPolicy = "investigation.read";

    /// <inheritdoc />
    public string Id => "investigation";

    /// <inheritdoc />
    public string DisplayName => "СледствиеAI";

    /// <inheritdoc />
    public string? Subtitle => "Следственное подразделение";

    /// <inheritdoc />
    public string? EmblemUrl => null;

    /// <inheritdoc />
    public IReadOnlyList<IModule> Modules { get; } =
    [
        // --- Секция «Дела»: сводка и реестр дел (ТФ-ДЕЛ-01..03); карточки дел и фигурантов —
        // вложенные маршруты /cases/{id} и /persons/{id} без пунктов меню.
        new ModuleDescriptor("dashboard", "/dashboard", "Дашборд", Icons.Material.Filled.Dashboard,
            typeof(Dashboard), ReadPolicy, GroupCases),
        new ModuleDescriptor("cases", "/cases", "Дела", Icons.Material.Filled.FolderOpen,
            typeof(Cases), ReadPolicy, GroupCases),

        // --- Секция «Медиа»: страницы объявляет САМ пакет (носители, «Поиск по лицу», «Верификация»);
        // профиль лишь включает их в реестр (ADR-0017/0022).
        .. MediaModule.Modules,

        // --- Секция «Документооборот»: «Документы дела» и остальные страницы пакета docflow.
        .. DocFlowModule.Modules,

        // --- Секция «Администрирование» (ТФ-АДМ-01..03). Учётные записи, допуски и журнал аудита даёт
        // ПАКЕТ (ADR-0023) — те же экраны, что у «ИнспекторAI»; роли и подразделения остаются за
        // профилем: состав ролей и иерархия подразделений у каждого эксплуатанта свои. Страницы видны
        // всем (в claim'ах сессии роли нет), обработчики отклоняют вызывающего без права (ТБ-012).
        .. AdminModule.Modules,
        new ModuleDescriptor("admin-roles", "/admin/roles", "Роли пользователей",
            Icons.Material.Filled.AdminPanelSettings, typeof(UserRoles), ReadPolicy, GroupAdmin),
        new ModuleDescriptor("admin-divisions", "/admin/divisions", "Подразделения",
            Icons.Material.Filled.AccountTree, typeof(Divisions), ReadPolicy, GroupAdmin),
    ];

    /// <inheritdoc />
    /// <remarks>
    /// Колокольчик уведомлений даёт пакет docflow (порядок 10), виджеты пакета «Медиа» — сам пакет,
    /// кнопку «Сменить пароль» (диалог с любого экрана) — пакет «Администрирование» (порядок 20):
    /// своей копии профиль больше не держит (ADR-0023).
    /// </remarks>
    public IReadOnlyList<IShellWidget> ShellWidgets { get; } =
    [
        .. DocFlowModule.ShellWidgets,
        .. MediaModule.ShellWidgets,
        .. AdminModule.ShellWidgets,
    ];

    /// <inheritdoc />
    /// <remarks>Языковых моделей профиль не использует (ADR-0021): распознавание лиц — конвейер пакета «Медиа».</remarks>
    public IReadOnlyList<IModelContributor> ModelContributors { get; } = [];

    /// <inheritdoc />
    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        // Сценарии профиля: валидаторы (обработчики Mediator регистрирует source-генератор хоста).
        services.AddInvestigationApplication();

        // Прикладные сервисы подключённых пакетов (ADR-0017): у «Медиа» — с конфигурацией (модели, пороги),
        // у «Администрирования» — валидаторы сценариев и стартовая сверка допусков со справочником.
        DocFlowModule.RegisterServices(services);
        MediaModule.RegisterServices(services, configuration);
        AdminModule.RegisterServices(services);
    }

    /// <inheritdoc />
    public void RegisterDataContexts(IServiceCollection services, IConfiguration configuration)
    {
        // Контекст профиля (схема investigation) и реализации портов ОБОИХ пакетов; здесь же
        // IAccessPolicy профиля переопределяет заглушку ядра (ТБ-020) — вызывать после ядра.
        services.AddInvestigationPersistence(configuration);

        // Контексты пакетов — своя схема и своя история миграций у каждого (ADR-0017).
        DocFlowModule.RegisterDataContexts(services, configuration);
        MediaModule.RegisterDataContexts(services, configuration);

        // Контракт пакетов проверяется ЗДЕСЬ, а не «когда-нибудь при первом запросе»: контейнер ASP.NET
        // валидирует граф при сборке только в Development, в Production незакрытый порт всплыл бы как 500
        // на первом обращении к обработчику.
        EnsureModulePortsRegistered(services);
    }

    /// <summary>
    /// Проверить, что профиль закрыл КАЖДЫЙ порт подключённых пакетов (<see cref="DocFlowModule.RequiredServices"/>,
    /// <see cref="MediaModule.RequiredServices"/>, <see cref="AdminModule.RequiredServices"/>): отсутствие
    /// любого — <see cref="InvalidOperationException"/>
    /// с именем порта ещё на регистрации сервисов, то есть до старта хоста, в любом окружении (ТС-013).
    /// </summary>
    /// <param name="services">Коллекция сервисов после регистраций профиля и пакетов.</param>
    /// <exception cref="InvalidOperationException">Хотя бы один порт пакета не зарегистрирован.</exception>
    public static void EnsureModulePortsRegistered(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var missing = DocFlowModule.RequiredServices
            .Concat(MediaModule.RequiredServices)
            .Concat(AdminModule.RequiredServices)
            .Where(port => !services.Any(d => d.ServiceType == port))
            .Select(port => port.FullName ?? port.Name)
            .ToList();

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"Профиль «{nameof(InvestigationProfile)}» не закрыл порты подключённых пакетов: {string.Join(", ", missing)}. "
                + "Без реализации порта приложение не запускается (ТС-013): зарегистрируйте её в AddInvestigationPersistence.");
        }
    }

    /// <summary>
    /// Сырые HTTP-эндпоинты профиля — вызывается ХОСТОМ напрямую на конкретном типе (не через
    /// <see cref="IProfile"/>, ADR-0002): раздача файлов документооборота и носителей/вырезок пакета
    /// «Медиа» (ТБ-073). Делегирует подключённым пакетам.
    /// </summary>
    /// <param name="endpoints">Построитель маршрутов приложения.</param>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static",
        Justification = "Хост вызывает метод на экземпляре профиля (profile.MapEndpoints(app)) — та же форма, что у InspectorProfile.")]
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        DocFlowModule.MapEndpoints(endpoints);
        MediaModule.MapEndpoints(endpoints);
    }
}

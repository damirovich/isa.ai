using ISC.AI.Abstractions.Modules;
using ISC.AI.Modules.Admin.Application;
using ISC.AI.Modules.Admin.UI;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;

namespace ISC.AI.Modules.Admin;

/// <summary>
/// Манифест пакета модулей «Администрирование платформы» (ADR-0017/0023): учётные записи, допуски,
/// неизменяемый журнал аудита и смена своего пароля — то, что одинаково нужно КАЖДОМУ профилю и не
/// зависит от его домена. Профиль включает страницы в свой реестр и реализует три порта.
/// </summary>
/// <remarks>
/// Почему пакет, а не копия в каждом профиле: экраны опираются исключительно на порты ЯДРА
/// (<c>IUserAccountStore</c>, <c>IClearanceStore</c>, <c>IAuditReader</c>) и схему <c>core</c> —
/// доменного в них нет ничего. До выделения они жили в профиле «ИнспекторAI», и второму профилю
/// пришлось бы скопировать около 1900 строк, после чего два экрана прав доступа расходились бы
/// независимо — самый опасный вид дублирования (ADR-0002: следующий отдел подключает, не копируя).
///
/// Слоя данных у пакета НЕТ намеренно: своей схемы он не заводит, потому что администрирует данные
/// ядра. Поэтому <c>RegisterDataContexts</c> здесь тоже нет.
/// </remarks>
public static class AdminModule
{
    /// <summary>Секция меню, под которой профиль группирует страницы администрирования.</summary>
    public const string MenuGroup = "Администрирование";

    /// <summary>Политика доступа страниц пакета (регистрируется хостом из реестра профиля).</summary>
    public const string ReadPolicy = "admin.read";

    /// <summary>
    /// ЧТО ПРОФИЛЬ ОБЯЗАН ПРЕДОСТАВИТЬ. Без любого порта контейнер не соберёт обработчик и приложение
    /// не запустится — намеренно: молчаливая заглушка вместо правила о доступе опаснее остановки.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><c>IPlatformAdministration</c> — кто вправе вести учётки и допуски и кто читает журнал
    /// (право определяется РОЛЬЮ, а роли ведёт профиль);</item>
    /// <item><c>IDivisionCatalog</c> — наименования подразделений для экрана допусков (ядро знает
    /// подразделение только номером);</item>
    /// <item><c>IUserRoleCatalog</c> — роли и назначения для колонки и фильтра на экране учётных
    /// записей (пакет роли только показывает, назначает их страница профиля).</item>
    /// </list>
    /// Список закреплён тестом <c>AdminContractTests</c>: он не должен разрастаться незаметно.
    /// </remarks>
    public static IReadOnlyList<Type> RequiredServices { get; } =
    [
        typeof(Domain.Services.IPlatformAdministration),
        typeof(Domain.Services.IDivisionCatalog),
        typeof(Domain.Services.IUserRoleCatalog),
    ];

    /// <summary>
    /// Нейтральные службы ядра, которыми пакет пользуется и которые даёт хост: реестр учётных записей,
    /// допуски, чтение журнала, субъект и контекст доступа, писатель журнала.
    /// </summary>
    public static IReadOnlyList<Type> RequiredCoreServices { get; } =
    [
        typeof(Abstractions.Security.IUserAccountStore),
        typeof(Abstractions.Security.IClearanceStore),
        typeof(Abstractions.Security.ISubjectProvider),
        typeof(Abstractions.Security.IAccessContextProvider),
        typeof(Abstractions.Audit.IAuditReader),
        typeof(Abstractions.Audit.IAuditWriter),
    ];

    /// <summary>
    /// Реестр страниц пакета — профиль подмешивает его в свой <c>IProfile.Modules</c>.
    /// Страница смены своего пароля (<c>/account/password</c>) в реестр НЕ входит: она без пункта меню,
    /// на неё уводит хост при временном пароле, и работает она потому, что сборка пакета уже
    /// смонтирована страницами ниже.
    /// </summary>
    public static IReadOnlyList<IModule> Modules { get; } =
    [
        new ModuleDescriptor("admin-users", "/admin/users", "Учётные записи",
            Icons.Material.Filled.ManageAccounts, typeof(UserAccounts), ReadPolicy, MenuGroup),
        new ModuleDescriptor("admin-clearances", "/admin/clearances", "Допуски пользователей",
            Icons.Material.Filled.Key, typeof(UserClearances), ReadPolicy, MenuGroup),
        new ModuleDescriptor("admin-audit", "/admin/audit", "Журнал аудита",
            Icons.Material.Filled.History, typeof(AuditJournal), ReadPolicy, MenuGroup),
    ];

    /// <summary>Виджеты оболочки: кнопка смены пароля в шапке (диалог с любого экрана).</summary>
    public static IReadOnlyList<IShellWidget> ShellWidgets { get; } =
    [
        new ShellWidgetDescriptor("account-password", ShellWidgetSlot.AppBarRight, Order: 20, typeof(AccountWidget)),
    ];

    /// <summary>Прикладные сервисы пакета — вызывается профилем в <c>IProfile.RegisterServices</c>.</summary>
    public static IServiceCollection RegisterServices(IServiceCollection services) =>
        services.AddAdminApplication();
}

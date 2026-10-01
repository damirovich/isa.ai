using ISC.AI.Abstractions.Security;

namespace ISC.AI.Profile.Inspector.Domain.Services;

/// <summary>Группа строк матрицы доступа на экране.</summary>
public enum PermissionSection
{
    /// <summary>Контроль: учёт нарушений.</summary>
    Control = 1,

    /// <summary>Нормативная база.</summary>
    Norms = 2,

    /// <summary>Организация работы: методики, подразделения.</summary>
    Organization = 3,

    /// <summary>Документооборот.</summary>
    DocFlow = 4,

    /// <summary>Администрирование.</summary>
    Administration = 5,
}

/// <summary>
/// Строка матрицы доступа (ADR-0033): право профиля, его подпись и пояснение для экрана, умолчание поставки и замки.
/// </summary>
/// <param name="Key">Ключ права — хранится в <c>inspector.role_permission</c>, менять нельзя.</param>
/// <param name="Section">Группа на экране.</param>
/// <param name="Label">Подпись строки.</param>
/// <param name="Hint">Пояснение под подписью: что именно открывает право.</param>
/// <param name="Defaults">Роли, которым право открыто по умолчанию (правила поставки).</param>
/// <param name="OpenDuringInitialSetup">
/// Пока в контуре нет ни одного Администратора, право открыто любому вошедшему (режим первичной настройки, «замок без
/// ключа» 6.4.1 — иначе систему невозможно ни настроить, ни наполнить).
/// </param>
/// <param name="AdministratorOnlyReason">
/// Если задано — строка закреплена за Администратором и галочками не меняется; текст объясняет почему.
/// </param>
public sealed record PermissionDefinition(
    string Key,
    PermissionSection Section,
    string Label,
    string Hint,
    IReadOnlySet<UserRole> Defaults,
    bool OpenDuringInitialSetup = false,
    string? AdministratorOnlyReason = null);

/// <summary>Раздел меню и права, которые его открывают.</summary>
/// <param name="ModuleId">Идентификатор модуля профиля.</param>
/// <param name="Label">Подпись раздела для предпросмотра меню.</param>
/// <param name="Permissions">
/// Права, любое из которых делает раздел видимым; пусто — раздел виден любому вошедшему (сервер его ролью не
/// ограничивает, и меню не прячет то, что сервер отдаёт).
/// </param>
public sealed record MenuSectionRule(string ModuleId, string Label, IReadOnlyList<string> Permissions);

/// <summary>
/// Матрица доступа профиля «ИнспекторAI» (§2.1 ТЗ СКИД, ADR-0033): какие действия и разделы открыты каждой роли.
/// Перечень прав, умолчания поставки, замки и соответствие «раздел меню → право» — здесь, в одном месте: по этой
/// таблице отвечают и сервер (<see cref="PermissionRule"/>), и меню, и экран «Матрица доступа».
/// </summary>
/// <remarks>
/// <para>
/// ИНВАРИАНТЫ (ТБ-012). (1) Итог ячейки — <see cref="IsGranted(PermissionDefinition, UserRole, bool?)"/>:
/// сохранённое Администратором значение, а если его нет — умолчание. В БД хранятся только отличия от умолчаний.
/// (2) Замкнутую ячейку (<see cref="LockReason"/>) хранимое значение НЕ меняет, даже если строку вписали в БД в
/// обход экрана: «Пользователи, роли и допуски» и «Матрица доступа» закреплены за Администратором — иначе сотрудник
/// выдал бы себе роль, допуск или новые права сам, а Администратор мог бы запереть систему без ключа.
/// (3) Неизвестный ключ права (удалён в новой версии) игнорируется. (4) Умолчания повторяют прежние жёсткие правила
/// сервера — закреплено тестом.
/// </para>
/// <para>
/// Чего матрица НЕ настраивает: решётку гриф/подразделение (ТБ-020/021, допуск — в карточке сотрудника), какие
/// именно документы видит роль (Инспектор — где он инспектор, Исполнитель — где у него поручение, <c>InspectorAccessPolicy</c>),
/// грунтовку ответов ИИ (инвариант 1 CLAUDE.md), неснимаемость Администратора в режиме первичной настройки.
/// </para>
/// </remarks>
public static class InspectorPermissions
{
    /// <summary>Учёт нарушений: запись и правка.</summary>
    public const string ViolationsEdit = "violations.edit";

    /// <summary>Картотека НПА: ведение норм, редакций и связок с корпусом.</summary>
    public const string NormsManage = "norms.manage";

    /// <summary>Методики: сохранение в реестр.</summary>
    public const string MethodsSave = "methods.save";

    /// <summary>Методики: утверждение, правка и удаление.</summary>
    public const string MethodsManage = "methods.manage";

    /// <summary>Подразделения: ведение справочника.</summary>
    public const string DivisionsManage = "divisions.manage";

    /// <summary>Документы и отчёты документооборота.</summary>
    public const string DocFlowView = "docflow.view";

    /// <summary>Типы документов и настройки документооборота.</summary>
    public const string DocFlowSettings = "docflow.settings";

    /// <summary>Снятие поручения с контроля — финальный статус (ТЗ СКИД §4.2: «только Руководитель»).</summary>
    public const string DocFlowClose = "docflow.close";

    /// <summary>Пользователи, роли и допуски.</summary>
    public const string AdminUsers = "admin.users";

    /// <summary>Матрица доступа.</summary>
    public const string AdminMatrix = "admin.matrix";

    /// <summary>Классификатор видов нарушений.</summary>
    public const string AdminDirectories = "admin.directories";

    /// <summary>Журнал аудита.</summary>
    public const string AdminAudit = "admin.audit";

    private static readonly UserRole[] AllRoles = Enum.GetValues<UserRole>();

    /// <summary>Все права профиля в порядке показа на экране.</summary>
    public static IReadOnlyList<PermissionDefinition> All { get; } =
    [
        new(ViolationsEdit, PermissionSection.Control, "Учёт нарушений: запись и правка",
            "Заносить и править нарушения. Смотреть реестр может любая роль",
            Roles(UserRole.Inspector, UserRole.Manager, UserRole.Administrator), OpenDuringInitialSetup: true),

        new(NormsManage, PermissionSection.Norms, "Картотека НПА: ведение",
            "Нормы, редакции и их статусы, связки с корпусом, сверка с реестром НПА. Статус редакции меняет выдачу поиска и грунтовки для всех",
            Roles(UserRole.Administrator), OpenDuringInitialSetup: true),

        new(MethodsSave, PermissionSection.Organization, "Методики: сохранение в реестр",
            "Сохранить методику проверки как черновик",
            Roles(UserRole.Inspector, UserRole.Manager, UserRole.Administrator), OpenDuringInitialSetup: true),
        new(MethodsManage, PermissionSection.Organization, "Методики: утверждение, правка, удаление",
            "Утверждённая методика становится эталоном для всех",
            Roles(UserRole.Manager, UserRole.Administrator), OpenDuringInitialSetup: true),
        new(DivisionsManage, PermissionSection.Organization, "Подразделения: ведение",
            "Создание, переименование, выключение и удаление территориальных и линейных подразделений",
            Roles(AllRoles), OpenDuringInitialSetup: true),

        new(DocFlowView, PermissionSection.DocFlow, "Документы и отчёты",
            "Документы в пределах допуска: Администратор и Руководитель — все, Инспектор — где он инспектор, Исполнитель — где у него поручение",
            Roles(AllRoles)),
        new(DocFlowClose, PermissionSection.DocFlow, "Снятие поручения с контроля",
            "Финальный статус «Снято с контроля»: после него поручение не меняется. По правилам документооборота — Руководитель",
            Roles(UserRole.Manager)),
        new(DocFlowSettings, PermissionSection.DocFlow, "Типы документов и настройки",
            "Настройка документооборота",
            Roles(UserRole.Administrator), OpenDuringInitialSetup: true),

        new(AdminUsers, PermissionSection.Administration, "Пользователи, роли и допуски",
            "Учётные записи, назначение ролей, выдача допусков",
            Roles(UserRole.Administrator), OpenDuringInitialSetup: true,
            AdministratorOnlyReason: "Закреплено за Администратором: иначе сотрудник смог бы выдать себе роль или допуск, "
                + "а без этого права у Администратора систему некому было бы настроить."),
        new(AdminMatrix, PermissionSection.Administration, "Матрица доступа",
            "Эта таблица",
            Roles(UserRole.Administrator), OpenDuringInitialSetup: true,
            AdministratorOnlyReason: "Закреплено за Администратором: иначе роль смогла бы сама расширить свои права, "
                + "а без этого права у Администратора матрицу некому было бы исправить."),
        new(AdminDirectories, PermissionSection.Administration, "Виды нарушений",
            "Классификатор «сфера → вид» для учёта нарушений",
            Roles(UserRole.Administrator), OpenDuringInitialSetup: true),
        new(AdminAudit, PermissionSection.Administration, "Журнал аудита",
            "Чтение журнала в пределах допуска",
            Roles(UserRole.Administrator), OpenDuringInitialSetup: true),
    ];

    /// <summary>
    /// Разделы меню профиля и права, которые их открывают: меню считает видимость по ТЕМ ЖЕ правам, что проверяет
    /// сервер. Раздел с пустым списком сервер ролью не ограничивает — он виден любому вошедшему, как и раньше.
    /// Раздел, которого здесь нет, тоже виден любому вошедшему — прятать молча нельзя (тест требует полноты).
    /// </summary>
    public static IReadOnlyList<MenuSectionRule> MenuSections { get; } =
    [
        new("dashboard", "Дашборд", []),
        new("violations", "Учёт нарушений", []),
        new("risks", "Риски и контроль", []),
        new("monitoring", "Мониторинг", []),
        new("archive", "Архив", []),
        new("generator", "Генератор", []),
        new("editor", "Редактор", []),
        new("analysis", "Анализ / Сравнение", []),
        new("chat", "Чат-ассистент", []),
        new("npa-search", "База НПА", []),
        new("npa-registry", "Картотека НПА", []),
        new("load", "Загрузка корпуса", []),
        new("methods", "Методики проверок", []),
        new("meetings", "Совещания", []),
        new("collegium", "Коллегия", []),
        new("docflow-documents", "Документы", [DocFlowView]),
        new("docflow-reports", "Отчёты", [DocFlowView]),
        new("docflow-types", "Типы документов", [DocFlowSettings]),
        new("docflow-settings", "Настройки документооборота", [DocFlowSettings]),
        new("divisions-territorial", "Территориальные", []),
        new("divisions-linear", "Линейные", []),
        new("admin-users", "Пользователи", [AdminUsers]),
        new("admin-access-matrix", "Матрица доступа", [AdminMatrix]),
        new("admin-audit", "Журнал аудита", [AdminAudit]),
        new("admin-violation-categories", "Виды нарушений", [AdminDirectories]),
    ];

    private static readonly Dictionary<string, PermissionDefinition> ByKey =
        All.ToDictionary(p => p.Key, StringComparer.Ordinal);

    /// <summary>Право по ключу; неизвестный ключ — <see langword="null"/>.</summary>
    public static PermissionDefinition? Find(string key) =>
        key is not null && ByKey.TryGetValue(key, out var permission) ? permission : null;

    /// <summary>Право по ключу; неизвестный ключ — ошибка программиста.</summary>
    public static PermissionDefinition Get(string key) =>
        Find(key) ?? throw new ArgumentException($"Неизвестное право «{key}».", nameof(key));

    /// <summary>Почему ячейку нельзя изменить галочкой; можно — <see langword="null"/>.</summary>
    public static string? LockReason(PermissionDefinition permission, UserRole role)
    {
        ArgumentNullException.ThrowIfNull(permission);

        // Замок — на всю строку: у Администратора право не снимается, остальным не выдаётся.
        return Enum.IsDefined(role) ? permission.AdministratorOnlyReason : null;
    }

    /// <summary>
    /// Итог ячейки: у замкнутой — всегда умолчание; иначе сохранённое значение (<paramref name="stored"/>), а если его
    /// нет — умолчание поставки.
    /// </summary>
    public static bool IsGranted(PermissionDefinition permission, UserRole role, bool? stored)
    {
        ArgumentNullException.ThrowIfNull(permission);
        return LockReason(permission, role) is null && stored is { } value ? value : permission.Defaults.Contains(role);
    }

    /// <summary>Итог ячейки по набору сохранённых отличий от умолчаний.</summary>
    public static bool IsGranted(PermissionDefinition permission, UserRole role, IReadOnlyCollection<RolePermissionOverride> overrides)
    {
        ArgumentNullException.ThrowIfNull(permission);
        ArgumentNullException.ThrowIfNull(overrides);
        var stored = overrides.FirstOrDefault(o => o.Role == role && string.Equals(o.Permission, permission.Key, StringComparison.Ordinal));
        return IsGranted(permission, role, stored?.IsGranted);
    }

    /// <summary>
    /// Есть ли право у пользователя с ролью <paramref name="role"/> (<see langword="null"/> — без роли): итог ячейки,
    /// а для прав с режимом первичной настройки — ещё и «в контуре нет ни одного Администратора». То же правило, что
    /// <see cref="PermissionRule.UserHasAsync"/>, над уже прочитанной матрицей (меню, предпросмотр).
    /// </summary>
    public static bool Has(
        PermissionDefinition permission,
        UserRole? role,
        bool initialSetup,
        IReadOnlyCollection<RolePermissionOverride> overrides)
    {
        ArgumentNullException.ThrowIfNull(permission);
        return (role is { } assigned && IsGranted(permission, assigned, overrides))
            || (permission.OpenDuringInitialSetup && initialSetup);
    }

    private static HashSet<UserRole> Roles(params UserRole[] roles) => [.. roles];
}

/// <summary>Сохранённое отличие ячейки матрицы от умолчания.</summary>
/// <param name="Role">Роль.</param>
/// <param name="Permission">Ключ права.</param>
/// <param name="IsGranted">Открыто ли право.</param>
/// <param name="UpdatedByUserId">Кто изменил.</param>
/// <param name="UpdatedAtUtc">Когда изменено.</param>
public sealed record RolePermissionOverride(
    UserRole Role, string Permission, bool IsGranted, int? UpdatedByUserId = null, DateTime? UpdatedAtUtc = null);

/// <summary>Изменение ячейки: <see cref="IsGranted"/> = <see langword="null"/> — вернуть к умолчанию (удалить отличие).</summary>
/// <param name="Role">Роль.</param>
/// <param name="Permission">Ключ права.</param>
/// <param name="IsGranted">Новое значение отличия или <see langword="null"/>.</param>
public sealed record RolePermissionChange(UserRole Role, string Permission, bool? IsGranted);

/// <summary>
/// Проверка права по матрице доступа (§2.1 ТЗ СКИД, ТБ-012, ADR-0033) — ОДНО правило для всех сценариев, портов
/// пакетов и меню профиля. Опора — «кто вошёл» (<see cref="ISubjectProvider"/>) и его роль, а НЕ контекст допуска:
/// на чистом контуре допуска нет ни у кого (6.4.2). Решётку гриф/подразделение (ТБ-020/021) правило не заменяет.
/// </summary>
/// <remarks>
/// Fail-closed: нет субъекта — отказ до всякого обращения к данным; нет роли — отказ, кроме прав с режимом первичной
/// настройки, пока в контуре нет ни одного Администратора (<see cref="PermissionDefinition.OpenDuringInitialSetup"/>).
/// Условие — именно «нет Администратора», а не «реестр ролей пуст»: иначе любая первая роль закрыла бы окно навсегда.
/// </remarks>
public static class PermissionRule
{
    /// <summary>Открыто ли право роли (с учётом сохранённого отличия и замков).</summary>
    public static async Task<bool> IsGrantedAsync(
        IUserRoleStore roles, UserRole role, string permission, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roles);
        var definition = InspectorPermissions.Get(permission);

        // Для замкнутой ячейки хранилище даже не спрашиваем: её значение не зависит от БД.
        if (InspectorPermissions.LockReason(definition, role) is not null)
        {
            return definition.Defaults.Contains(role);
        }

        var stored = await roles.GetPermissionOverrideAsync(role, permission, cancellationToken);
        return InspectorPermissions.IsGranted(definition, role, stored);
    }

    /// <summary>Есть ли право у пользователя (по его роли; без роли — только режим первичной настройки).</summary>
    public static async Task<bool> UserHasAsync(
        IUserRoleStore roles, int userId, string permission, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roles);
        var definition = InspectorPermissions.Get(permission);

        if (await roles.GetRoleAsync(userId, cancellationToken) is { } role
            && await IsGrantedAsync(roles, role, permission, cancellationToken))
        {
            return true;
        }

        return definition.OpenDuringInitialSetup && !await roles.AnyAdministratorAsync(cancellationToken);
    }

    /// <summary>Есть ли право у текущего субъекта. Без аутентификации — всегда отказ.</summary>
    public static async Task<bool> CallerHasAsync(
        IUserRoleStore roles, ISubjectProvider subjectProvider, string permission, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(subjectProvider);

        if (await subjectProvider.GetCurrentUserIdAsync(cancellationToken) is not { } userId)
        {
            return false;
        }

        return await UserHasAsync(roles, userId, permission, cancellationToken);
    }

    /// <summary>Текст отказа по праву: какое действие закрыто и к кому идти.</summary>
    public static string Denied(string permission) =>
        $"Действие «{InspectorPermissions.Get(permission).Label}» закрыто для вашей роли в матрице доступа. Обратитесь к Администратору.";
}

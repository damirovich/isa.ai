using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Domain.Enums;

namespace ISC.AI.Profile.Investigation.Domain.Services;

/// <summary>Группа строк матрицы доступа на экране.</summary>
public enum PermissionSection
{
    /// <summary>Дела.</summary>
    Cases = 1,

    /// <summary>Медиа: загрузка, поиск по лицу, верификация.</summary>
    Media = 2,

    /// <summary>Документооборот и сводки.</summary>
    DocFlow = 3,

    /// <summary>Администрирование.</summary>
    Administration = 4,
}

/// <summary>
/// Строка матрицы доступа (ADR-0032): право профиля, его подпись и пояснение для экрана, умолчание поставки и замки.
/// </summary>
/// <param name="Key">Ключ права — хранится в <c>investigation.role_permission</c>, менять нельзя.</param>
/// <param name="Section">Группа на экране.</param>
/// <param name="Label">Подпись строки.</param>
/// <param name="Hint">Пояснение под подписью: что именно открывает право.</param>
/// <param name="Defaults">Роли, которым право открыто по умолчанию (правила поставки).</param>
/// <param name="OpenDuringInitialSetup">
/// Пока в контуре нет ни одного Администратора, право открыто любому вошедшему (режим первичной настройки — иначе
/// назначить первого Администратора было бы некому).
/// </param>
/// <param name="AdministratorOnlyReason">
/// Если задано — строка закреплена за Администратором и галочками не меняется; текст объясняет почему.
/// </param>
public sealed record PermissionDefinition(
    string Key,
    PermissionSection Section,
    string Label,
    string Hint,
    IReadOnlySet<InvestigationRole> Defaults,
    bool OpenDuringInitialSetup = false,
    string? AdministratorOnlyReason = null);

/// <summary>Раздел меню и права, которые его открывают (достаточно любого из них).</summary>
/// <param name="ModuleId">Идентификатор модуля профиля.</param>
/// <param name="Label">Подпись раздела для предпросмотра меню.</param>
/// <param name="Permissions">Права, любое из которых делает раздел видимым.</param>
public sealed record MenuSectionRule(string ModuleId, string Label, IReadOnlyList<string> Permissions);

/// <summary>
/// Матрица доступа профиля «Следствие» (ТП-004, ADR-0032): какие разделы и действия открыты каждой роли. Перечень
/// прав, умолчания поставки, замки и соответствие «раздел меню → право» — здесь, в одном месте: по этой таблице
/// отвечают и сервер (<see cref="PermissionRule"/>), и меню, и экран «Матрица доступа».
/// </summary>
/// <remarks>
/// <para>
/// ИНВАРИАНТЫ (ТБ-012). (1) Итог ячейки — <see cref="IsGranted(PermissionDefinition, InvestigationRole, bool?)"/>:
/// сохранённое Администратором значение, а если его нет — умолчание. Строка в БД хранится только для ячеек, которые отличаются от умолчания. (2) Замкнутую ячейку
/// (<see cref="LockReason"/>) хранимое значение НЕ меняет, даже если строку вписали в БД в обход экрана: права
/// «Пользователи, роли и допуски» и «Матрица доступа» закреплены за Администратором — иначе сотрудник выдал бы себе
/// роль, допуск или новые права сам, а Администратор мог бы запереть систему без ключа; «Уничтожение дела» — по
/// решению заказчика (ADR-0025). (3) Неизвестный ключ права (удалён в новой версии) игнорируется.
/// </para>
/// <para>
/// Чего матрица НЕ настраивает: решётку гриф/подразделение (ТБ-020/021 — допуск выдаётся отдельно, в карточке
/// сотрудника), сужение Следователя до своих дел (ТБ-071), правило двух лиц при верификации (ТБ-073 — оно в модуле
/// «Медиа» и не отключается), неснимаемость последнего Администратора.
/// </para>
/// </remarks>
public static class InvestigationPermissions
{
    /// <summary>Дашборд и реестр дел: видимость дел (в пределах допуска).</summary>
    public const string CasesView = "cases.view";

    /// <summary>Заведение и правка дел, фигурантов, оснований поиска, документов дела и сводок.</summary>
    public const string CasesEdit = "cases.edit";

    /// <summary>Уничтожение дела (ADR-0025).</summary>
    public const string CasesPurge = "cases.purge";

    /// <summary>Загрузка материалов в дело.</summary>
    public const string MediaUpload = "media.upload";

    /// <summary>Поиск по лицу.</summary>
    public const string MediaSearch = "media.search";

    /// <summary>Гарантированное удаление носителя.</summary>
    public const string MediaPurge = "media.purge";

    /// <summary>Верификация — первая подпись (стадия эксперта).</summary>
    public const string VerificationExpert = "verification.expert";

    /// <summary>Верификация — вторая подпись (стадия верификатора).</summary>
    public const string VerificationVerifier = "verification.verifier";

    /// <summary>Верификация — итог руководителя при расхождении эксперта и верификатора (ТФ-ВЕР-02, ADR-0036).</summary>
    public const string VerificationResolve = "verification.resolve";

    /// <summary>Отзыв ошибочного появления (ADR-0034).</summary>
    public const string VerificationRevoke = "verification.revoke";

    /// <summary>Документы и отчёты документооборота.</summary>
    public const string DocFlowView = "docflow.view";

    /// <summary>Типы документов и настройки документооборота.</summary>
    public const string DocFlowSettings = "docflow.settings";

    /// <summary>Снятие поручения с контроля — финальный статус (ТЗ СКИД §4.2: «только Руководитель»).</summary>
    public const string DocFlowClose = "docflow.close";

    /// <summary>Решения по запросам на правку архивных сводок (ТФ-АДМ-06).</summary>
    public const string ReportPermits = "reports.permits";

    /// <summary>Пользователи, роли и допуски.</summary>
    public const string AdminUsers = "admin.users";

    /// <summary>Матрица доступа.</summary>
    public const string AdminMatrix = "admin.matrix";

    /// <summary>Подразделения и справочники.</summary>
    public const string AdminDirectories = "admin.directories";

    /// <summary>Журнал аудита.</summary>
    public const string AdminAudit = "admin.audit";

    private static readonly InvestigationRole[] AllRoles = Enum.GetValues<InvestigationRole>();

    /// <summary>Все права профиля в порядке показа на экране.</summary>
    public static IReadOnlyList<PermissionDefinition> All { get; } =
    [
        new(CasesView, PermissionSection.Cases, "Дашборд и реестр дел",
            "Дела в пределах допуска. Без этого права роль не видит ни одного дела и его материалов",
            Roles(AllRoles)),
        new(CasesEdit, PermissionSection.Cases, "Заведение и правка дел",
            "Дела, фигуранты, основания поиска, документы дела, сводки и справки",
            Roles(InvestigationRole.Investigator, InvestigationRole.Head, InvestigationRole.Administrator)),
        new(CasesPurge, PermissionSection.Cases, "Уничтожение дела",
            "Необратимо: дело со всеми материалами, с актом в журнале",
            Roles(InvestigationRole.Administrator),
            AdministratorOnlyReason: "Закреплено за Администратором (решение заказчика, ADR-0025): уничтожение необратимо, "
                + "и следователь не должен иметь возможности уничтожить своё же дело."),

        new(MediaUpload, PermissionSection.Media, "Загрузка материалов",
            "Фото, видео и аудио в дело",
            Roles(InvestigationRole.Investigator, InvestigationRole.Administrator)),
        new(MediaSearch, PermissionSection.Media, "Поиск по лицу",
            "По основанию поиска, в пределах доступных дел",
            Roles(InvestigationRole.Investigator, InvestigationRole.FaceExpert, InvestigationRole.Administrator)),
        new(MediaPurge, PermissionSection.Media, "Гарантированное удаление материала",
            "Носитель и шаблоны лиц — без восстановления",
            Roles(InvestigationRole.Administrator, InvestigationRole.Head)),
        new(VerificationExpert, PermissionSection.Media, "Верификация — первая подпись",
            "Эксперт привязывает найденное лицо к фигуранту",
            Roles(InvestigationRole.FaceExpert, InvestigationRole.Administrator)),
        new(VerificationVerifier, PermissionSection.Media, "Верификация — вторая подпись",
            "Вслепую, другим сотрудником: один человек обе подписи не ставит",
            Roles(InvestigationRole.Verifier, InvestigationRole.Administrator)),
        new(VerificationResolve, PermissionSection.Media, "Верификация — решение при расхождении",
            "Итог по кандидату, где эксперт и верификатор разошлись; подтвердить — только если один из них подтвердил",
            Roles(InvestigationRole.Head, InvestigationRole.Administrator)),
        new(VerificationRevoke, PermissionSection.Media, "Отзыв ошибочного появления",
            "С причиной; отзывает не тот, кто подтверждал. Пересечения пересчитываются",
            Roles(InvestigationRole.Head, InvestigationRole.Administrator)),

        new(DocFlowView, PermissionSection.DocFlow, "Документы и отчёты",
            "Документы документооборота в пределах допуска",
            Roles(AllRoles)),
        new(DocFlowClose, PermissionSection.DocFlow, "Снятие поручения с контроля",
            "Финальный статус «Снято с контроля»: после него поручение не меняется. По правилам документооборота — Руководитель",
            Roles(InvestigationRole.Head)),
        new(DocFlowSettings, PermissionSection.DocFlow, "Типы документов и настройки",
            "Настройка документооборота",
            Roles(InvestigationRole.Administrator), OpenDuringInitialSetup: true),
        new(ReportPermits, PermissionSection.DocFlow, "Разрешения на правку сводок",
            "Решение по запросам на правку архивной сводки или справки",
            Roles(InvestigationRole.Administrator)),

        new(AdminUsers, PermissionSection.Administration, "Пользователи, роли и допуски",
            "Учётные записи, назначение ролей, выдача допусков",
            Roles(InvestigationRole.Administrator), OpenDuringInitialSetup: true,
            AdministratorOnlyReason: "Закреплено за Администратором: иначе сотрудник смог бы выдать себе роль или допуск, "
                + "а без этого права у Администратора систему некому было бы настроить."),
        new(AdminMatrix, PermissionSection.Administration, "Матрица доступа",
            "Эта таблица",
            Roles(InvestigationRole.Administrator), OpenDuringInitialSetup: true,
            AdministratorOnlyReason: "Закреплено за Администратором: иначе роль смогла бы сама расширить свои права, "
                + "а без этого права у Администратора матрицу некому было бы исправить."),
        new(AdminDirectories, PermissionSection.Administration, "Подразделения и справочники",
            "Подразделения, звания, должности и другие справочники",
            Roles(InvestigationRole.Administrator), OpenDuringInitialSetup: true),
        new(AdminAudit, PermissionSection.Administration, "Журнал аудита",
            "Чтение журнала в пределах допуска",
            Roles(InvestigationRole.Administrator, InvestigationRole.SecurityOfficer), OpenDuringInitialSetup: true),
    ];

    /// <summary>
    /// Разделы меню профиля и права, которые их открывают: меню считает видимость по ТЕМ ЖЕ правам, что проверяет
    /// сервер. Раздел, которого здесь нет, виден любому вошедшему — прятать молча нельзя (тест требует полноты).
    /// </summary>
    public static IReadOnlyList<MenuSectionRule> MenuSections { get; } =
    [
        new("dashboard", "Дашборд", [CasesView]),
        new("cases", "Дела", [CasesView]),
        new("media-search", "Поиск по лицу", [MediaSearch]),
        new("media-verification", "Верификация", [VerificationExpert, VerificationVerifier, VerificationResolve]),
        new("docflow-documents", "Документы", [DocFlowView]),
        new("docflow-reports", "Отчёты", [DocFlowView]),
        new("docflow-types", "Типы документов", [DocFlowSettings]),
        new("docflow-settings", "Настройки документооборота", [DocFlowSettings]),
        new("admin-users", "Пользователи", [AdminUsers]),
        new("admin-access-matrix", "Матрица доступа", [AdminMatrix]),
        new("admin-audit", "Журнал аудита", [AdminAudit]),
        new("admin-divisions", "Подразделения", [AdminDirectories]),
        new("admin-references", "Справочники", [AdminDirectories]),
        new("admin-report-permits", "Запросы на правку сводок", [ReportPermits]),
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
    public static string? LockReason(PermissionDefinition permission, InvestigationRole role)
    {
        ArgumentNullException.ThrowIfNull(permission);

        // Замок — на всю строку: у Администратора право не снимается, остальным не выдаётся.
        return Enum.IsDefined(role) ? permission.AdministratorOnlyReason : null;
    }

    /// <summary>
    /// Итог ячейки: у замкнутой — всегда умолчание; иначе сохранённое значение (<paramref name="stored"/>), а если его
    /// нет — умолчание поставки.
    /// </summary>
    public static bool IsGranted(PermissionDefinition permission, InvestigationRole role, bool? stored)
    {
        ArgumentNullException.ThrowIfNull(permission);
        return LockReason(permission, role) is null && stored is { } value ? value : permission.Defaults.Contains(role);
    }

    /// <summary>Итог ячейки по набору сохранённых отличий от умолчаний.</summary>
    public static bool IsGranted(PermissionDefinition permission, InvestigationRole role, IReadOnlyCollection<RolePermissionOverride> overrides)
    {
        ArgumentNullException.ThrowIfNull(permission);
        ArgumentNullException.ThrowIfNull(overrides);
        var stored = overrides.FirstOrDefault(o => o.Role == role && string.Equals(o.Permission, permission.Key, StringComparison.Ordinal));
        return IsGranted(permission, role, stored?.IsGranted);
    }

    /// <summary>
    /// Есть ли право у пользователя с ролью <paramref name="role"/> (<see langword="null"/> — без роли): итог ячейки
    /// матрицы, а для прав с режимом первичной настройки — ещё и «в контуре нет ни одного Администратора». То же
    /// правило, что <see cref="PermissionRule.UserHasAsync"/>, над уже прочитанной матрицей (меню, предпросмотр).
    /// </summary>
    public static bool Has(
        PermissionDefinition permission,
        InvestigationRole? role,
        bool initialSetup,
        IReadOnlyCollection<RolePermissionOverride> overrides)
    {
        ArgumentNullException.ThrowIfNull(permission);
        return (role is { } assigned && IsGranted(permission, assigned, overrides))
            || (permission.OpenDuringInitialSetup && initialSetup);
    }

    private static HashSet<InvestigationRole> Roles(params InvestigationRole[] roles) => [.. roles];
}

/// <summary>Сохранённое отличие ячейки матрицы от умолчания.</summary>
/// <param name="Role">Роль.</param>
/// <param name="Permission">Ключ права.</param>
/// <param name="IsGranted">Открыто ли право.</param>
/// <param name="UpdatedByUserId">Кто изменил.</param>
/// <param name="UpdatedAtUtc">Когда изменено.</param>
public sealed record RolePermissionOverride(
    InvestigationRole Role, string Permission, bool IsGranted, int? UpdatedByUserId = null, DateTime? UpdatedAtUtc = null);

/// <summary>Изменение ячейки: <see cref="IsGranted"/> = <see langword="null"/> — вернуть к умолчанию (удалить отличие).</summary>
/// <param name="Role">Роль.</param>
/// <param name="Permission">Ключ права.</param>
/// <param name="IsGranted">Новое значение отличия или <see langword="null"/>.</param>
public sealed record RolePermissionChange(InvestigationRole Role, string Permission, bool? IsGranted);

/// <summary>
/// Проверка права по матрице доступа (ТП-004, ТБ-012, ADR-0032) — ОДНО правило для всех сценариев, портов пакетов и
/// меню профиля. Опора — «кто вошёл» (<see cref="ISubjectProvider"/>) и его роль; решётку гриф/подразделение
/// (ТБ-020/021) правило не заменяет, а дополняет.
/// </summary>
/// <remarks>
/// Fail-closed: нет субъекта — отказ до всякого обращения к данным; нет роли — отказ, кроме прав с режимом первичной
/// настройки, пока в контуре нет ни одного Администратора (<see cref="PermissionDefinition.OpenDuringInitialSetup"/>).
/// </remarks>
public static class PermissionRule
{
    /// <summary>Открыто ли право роли (с учётом сохранённого отличия и замков).</summary>
    public static async Task<bool> IsGrantedAsync(
        IUserRoleStore roles, InvestigationRole role, string permission, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roles);
        var definition = InvestigationPermissions.Get(permission);

        // Для замкнутой ячейки хранилище даже не спрашиваем: её значение не зависит от БД.
        if (InvestigationPermissions.LockReason(definition, role) is not null)
        {
            return definition.Defaults.Contains(role);
        }

        var stored = await roles.GetPermissionOverrideAsync(role, permission, cancellationToken);
        return InvestigationPermissions.IsGranted(definition, role, stored);
    }

    /// <summary>Есть ли право у пользователя (по его роли; без роли — только режим первичной настройки).</summary>
    public static async Task<bool> UserHasAsync(
        IUserRoleStore roles, int userId, string permission, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roles);
        var definition = InvestigationPermissions.Get(permission);

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

    /// <summary>
    /// Роль, под которой пользователь видит дела: его роль, если у неё открыто <see cref="InvestigationPermissions.CasesView"/>;
    /// иначе <see langword="null"/> — и правило видимости дел вернёт пусто (ТБ-012/021).
    /// </summary>
    public static async Task<InvestigationRole?> ResolveCaseViewerAsync(
        IUserRoleStore roles, int? userId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roles);

        if (userId is not { } id || await roles.GetRoleAsync(id, cancellationToken) is not { } role)
        {
            return null;
        }

        return await IsGrantedAsync(roles, role, InvestigationPermissions.CasesView, cancellationToken) ? role : null;
    }
}

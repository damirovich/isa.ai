using ISC.AI.Abstractions.Modules;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;

namespace ISC.AI.Profile.Investigation.Data;

/// <summary>
/// Разделы меню «Следствия» по роли (ТП-004): пользователь видит только те разделы, где его роль что-то может.
/// </summary>
/// <remarks>
/// ИНВАРИАНТ: таблица повторяет проверки СЕРВЕРА, а не заменяет их (ТБ-012) — прячется ровно то, где сценарии и так
/// откажут: поиск по лицу — <c>MediaAdministration.CanSearchAsync</c>; верификация — <c>VerificationPolicy</c>;
/// учётные записи, допуски, роли, подразделения, справочники, настройки документооборота —
/// <see cref="AdministrationRule.CallerCanManageAsync"/> (с режимом первичной настройки: пока Администратора нет,
/// эти разделы видит любой вошедший, иначе роль назначить некому); журнал аудита — ещё и Офицер ИБ; запросы на
/// правку сводок — только Администратор. Пользователь без роли видит лишь «Главную»: сценарии профиля ему отказывают.
/// Раздел, которого нет в таблице (новый модуль пакета), виден любой роли — прятать молча нельзя; тест требует,
/// чтобы каждый модуль профиля был в таблице явно.
/// </remarks>
public sealed class InvestigationModuleVisibility(IUserRoleStore roles, ISubjectProvider subjectProvider) : IModuleVisibility
{
    /// <summary>Кому виден раздел.</summary>
    public enum Audience
    {
        /// <summary>Любой роли профиля.</summary>
        AnyRole,

        /// <summary>Тем, кто вправе искать по лицу: Следователь, Эксперт по лицам, Администратор.</summary>
        FaceSearch,

        /// <summary>Эксперт по лицам, Верификатор, Администратор.</summary>
        Verification,

        /// <summary>Администратор (и любой вошедший, пока Администратора нет).</summary>
        Management,

        /// <summary>Как <see cref="Management"/> плюс Офицер ИБ.</summary>
        Audit,

        /// <summary>Только Администратор (без режима первичной настройки).</summary>
        AdministratorOnly,
    }

    /// <summary>Таблица «раздел → кому виден» по идентификаторам модулей профиля.</summary>
    public static IReadOnlyDictionary<string, Audience> Rules { get; } = new Dictionary<string, Audience>(StringComparer.Ordinal)
    {
        ["dashboard"] = Audience.AnyRole,
        ["cases"] = Audience.AnyRole,
        ["media-search"] = Audience.FaceSearch,
        ["media-verification"] = Audience.Verification,
        ["docflow-documents"] = Audience.AnyRole,
        ["docflow-reports"] = Audience.AnyRole,
        ["docflow-types"] = Audience.Management,
        ["docflow-settings"] = Audience.Management,
        ["admin-users"] = Audience.Management,
        ["admin-clearances"] = Audience.Management,
        ["admin-audit"] = Audience.Audit,
        ["admin-roles"] = Audience.Management,
        ["admin-divisions"] = Audience.Management,
        ["admin-references"] = Audience.Management,
        ["admin-report-permits"] = Audience.AdministratorOnly,
    };

    /// <inheritdoc />
    public async Task<IReadOnlySet<string>> GetVisibleModuleIdsAsync(
        IReadOnlyCollection<IModule> modules, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modules);

        var userId = await subjectProvider.GetCurrentUserIdAsync(cancellationToken);
        var role = userId is { } id ? await roles.GetRoleAsync(id, cancellationToken) : null;
        var canManage = await AdministrationRule.CallerCanManageAsync(roles, subjectProvider, cancellationToken);
        return Visible(modules.Select(m => m.Id), role, canManage);
    }

    /// <summary>Видимые разделы для роли; <paramref name="canManage"/> — право администрирования с режимом первичной настройки.</summary>
    public static IReadOnlySet<string> Visible(IEnumerable<string> moduleIds, InvestigationRole? role, bool canManage)
    {
        ArgumentNullException.ThrowIfNull(moduleIds);
        return moduleIds.Where(id => IsVisible(id, role, canManage)).ToHashSet(StringComparer.Ordinal);
    }

    private static bool IsVisible(string moduleId, InvestigationRole? role, bool canManage) =>
        (Rules.TryGetValue(moduleId, out var audience) ? audience : Audience.AnyRole) switch
        {
            Audience.AnyRole => role is not null || canManage,
            Audience.FaceSearch => role is InvestigationRole.Investigator or InvestigationRole.FaceExpert or InvestigationRole.Administrator,
            Audience.Verification => role is InvestigationRole.FaceExpert or InvestigationRole.Verifier or InvestigationRole.Administrator,
            Audience.Management => canManage,
            Audience.Audit => canManage || role is InvestigationRole.SecurityOfficer,
            Audience.AdministratorOnly => role is InvestigationRole.Administrator,
            _ => false,
        };
}

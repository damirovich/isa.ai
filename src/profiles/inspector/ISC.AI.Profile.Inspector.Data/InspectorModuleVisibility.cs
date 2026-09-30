using ISC.AI.Abstractions.Modules;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Domain.Services;

namespace ISC.AI.Profile.Inspector.Data;

/// <summary>
/// Разделы меню «ИнспекторAI» по матрице доступа (§2.1 ТЗ СКИД, ADR-0033): раздел, закрытый правом, виден только
/// тем ролям, у которых это право открыто.
/// </summary>
/// <remarks>
/// ИНВАРИАНТ: меню считает видимость по ТЕМ ЖЕ правам и той же таблице, что проверяет сервер (ТБ-012), —
/// соответствие «раздел → право» лежит в домене (<see cref="InspectorPermissions.MenuSections"/>), итог ячейки —
/// <see cref="InspectorPermissions.Has"/>. Разделы, которые сервер ролью не ограничивает (чат, база НПА, отчёты
/// руководству и т. п.), видны любому вошедшему, как и до матрицы: меню не прячет то, что сервер всё равно отдаёт.
/// </remarks>
public sealed class InspectorModuleVisibility(IUserRoleStore roles, ISubjectProvider subjectProvider) : IModuleVisibility
{
    /// <summary>Таблица «раздел → права, любое из которых его открывает» по идентификаторам модулей профиля.</summary>
    public static IReadOnlyDictionary<string, MenuSectionRule> Rules { get; } =
        InspectorPermissions.MenuSections.ToDictionary(r => r.ModuleId, StringComparer.Ordinal);

    /// <inheritdoc />
    public async Task<IReadOnlySet<string>> GetVisibleModuleIdsAsync(
        IReadOnlyCollection<IModule> modules, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modules);

        // Без аутентификации — ничего (fail-closed до всякого обращения к данным).
        if (await subjectProvider.GetCurrentUserIdAsync(cancellationToken) is not { } userId)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var role = await roles.GetRoleAsync(userId, cancellationToken);
        var overrides = await roles.ListPermissionOverridesAsync(cancellationToken) ?? [];
        var initialSetup = !await roles.AnyAdministratorAsync(cancellationToken);
        return Visible(modules.Select(m => m.Id), role, initialSetup, overrides);
    }

    /// <summary>
    /// Видимые разделы для роли по матрице: <paramref name="initialSetup"/> — в контуре нет ни одного Администратора,
    /// <paramref name="overrides"/> — сохранённые отличия матрицы от умолчаний.
    /// </summary>
    public static IReadOnlySet<string> Visible(
        IEnumerable<string> moduleIds,
        UserRole? role,
        bool initialSetup,
        IReadOnlyCollection<RolePermissionOverride> overrides)
    {
        ArgumentNullException.ThrowIfNull(moduleIds);
        ArgumentNullException.ThrowIfNull(overrides);

        return moduleIds
            .Where(id => !Rules.TryGetValue(id, out var rule)
                || rule.Permissions.Count == 0
                || rule.Permissions.Any(p => InspectorPermissions.Has(InspectorPermissions.Get(p), role, initialSetup, overrides)))
            .ToHashSet(StringComparer.Ordinal);
    }
}

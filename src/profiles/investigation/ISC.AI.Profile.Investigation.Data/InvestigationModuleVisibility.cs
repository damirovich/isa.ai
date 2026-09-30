using ISC.AI.Abstractions.Modules;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;

namespace ISC.AI.Profile.Investigation.Data;

/// <summary>
/// Разделы меню «Следствия» по матрице доступа (ТП-004, ADR-0032): пользователь видит только те разделы, право на
/// которые у его роли открыто.
/// </summary>
/// <remarks>
/// ИНВАРИАНТ: меню считает видимость по ТЕМ ЖЕ правам и той же таблице, что проверяет сервер (ТБ-012), — соответствие
/// «раздел → право» лежит в домене (<see cref="InvestigationPermissions.MenuSections"/>), итог ячейки —
/// <see cref="InvestigationPermissions.Has"/>. Меню проверки сервера не заменяет: прячется ровно то, где сценарии и
/// так откажут. Пока Администратора нет (режим первичной настройки), разделы администрирования видит любой вошедший —
/// иначе роль назначить некому. Раздел, которого нет в таблице (новый модуль пакета), виден любой роли — прятать
/// молча нельзя; тест требует, чтобы каждый модуль профиля был в таблице явно.
/// </remarks>
public sealed class InvestigationModuleVisibility(IUserRoleStore roles, ISubjectProvider subjectProvider) : IModuleVisibility
{
    /// <summary>Таблица «раздел → права, любое из которых его открывает» по идентификаторам модулей профиля.</summary>
    public static IReadOnlyDictionary<string, MenuSectionRule> Rules { get; } =
        InvestigationPermissions.MenuSections.ToDictionary(r => r.ModuleId, StringComparer.Ordinal);

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
        InvestigationRole? role,
        bool initialSetup,
        IReadOnlyCollection<RolePermissionOverride> overrides)
    {
        ArgumentNullException.ThrowIfNull(moduleIds);
        ArgumentNullException.ThrowIfNull(overrides);

        return moduleIds
            .Where(id => Rules.TryGetValue(id, out var rule)
                ? rule.Permissions.Any(p => InvestigationPermissions.Has(InvestigationPermissions.Get(p), role, initialSetup, overrides))
                : role is not null || initialSetup)
            .ToHashSet(StringComparer.Ordinal);
    }
}

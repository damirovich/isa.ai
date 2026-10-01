using ISC.AI.Modules.Admin.Domain.Services;
using ISC.AI.Profile.Inspector.Domain.Enums;
using ISC.AI.Profile.Inspector.Domain.Services;

namespace ISC.AI.Profile.Inspector.Data;

/// <summary>
/// Реализация порта <see cref="IUserRoleCatalog"/> — роли профиля «ИнспекторAI» (§2.1 ТЗ СКИД) для
/// колонки «Роль» и фильтра на экране учётных записей пакета администрирования.
/// </summary>
/// <remarks>
/// Ключ роли — имя элемента перечисления <see cref="UserRole"/> (<c>ToString()</c>): пакет его не
/// толкует, а только сравнивает, поэтому втаскивать в пакет тип профиля не нужно (ТС-009). Подпись
/// берётся из единственного места, где она записана (<see cref="UserRoleLabels"/>), — копия подписи
/// на стороне пакета разъехалась бы с профилем незаметно.
///
/// Назначение роли идёт с карточки сотрудника пакета (экран «Пользователи»): пакет проверяет право вызывающего,
/// профиль — сам ключ, то, что учётная запись действующая, и что не снимается последний Администратор
/// (<see cref="RoleAssignmentRule"/>).
/// </remarks>
public sealed class InspectorUserRoleCatalog(IUserRoleStore roles) : IUserRoleCatalog
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<RoleOption>> ListRolesAsync(CancellationToken cancellationToken = default)
    {
        // Описание строится по ДЕЙСТВУЮЩЕЙ матрице доступа (ADR-0033): после правки галочек карточка сотрудника
        // говорит правду о том, что даёт роль, а не пересказывает умолчания поставки.
        var overrides = await roles.ListPermissionOverridesAsync(cancellationToken) ?? [];
        return Enum.GetValues<UserRole>()
            .Select(role => new RoleOption(role.ToString(), role.Label(), Describe(role, overrides)))
            .ToList();
    }

    /// <inheritdoc />
    /// <remarks>Пользователи без назначенной роли в словарь не попадают — так же, как требует порт.</remarks>
    public async Task<IReadOnlyDictionary<int, string>> GetUserRoleKeysAsync(
        CancellationToken cancellationToken = default)
    {
        var rows = await roles.ListAsync(cancellationToken);

        return rows
            .Where(row => row.Role is not null)
            .ToDictionary(row => row.UserId, row => row.Role!.Value.ToString());
    }

    /// <inheritdoc />
    public async Task<RoleAssignmentResult> AssignAsync(int userId, string? roleKey, CancellationToken cancellationToken = default)
    {
        UserRole? role = null;
        if (roleKey is not null)
        {
            if (!Enum.TryParse<UserRole>(roleKey, ignoreCase: false, out var parsed)
                || !Enum.IsDefined(parsed)
                || !string.Equals(parsed.ToString(), roleKey, StringComparison.Ordinal))
            {
                return RoleAssignmentResult.Fail("Неизвестная роль.");
            }

            role = parsed;
        }

        // Роль назначается только действующему пользователю: реестр ролей строится по активным учётным записям.
        if (!(await roles.ListAsync(cancellationToken)).Any(row => row.UserId == userId))
        {
            return RoleAssignmentResult.Fail("Пользователь не найден или его учётная запись отключена.");
        }

        if (await RoleAssignmentRule.CheckAsync(roles, userId, role, cancellationToken) is { } refusal)
        {
            return RoleAssignmentResult.Fail(refusal);
        }

        await roles.SetRoleAsync(userId, role, cancellationToken);
        return RoleAssignmentResult.Ok;
    }

    /// <summary>
    /// Что даёт роль — по правам матрицы доступа, которые проверяет сервер (ADR-0033), и по неизменяемому правилу
    /// видимости документов (<see cref="InspectorAccessPolicy"/>, §2.1 ТЗ СКИД).
    /// </summary>
    public static string Describe(UserRole role, IReadOnlyCollection<RolePermissionOverride> overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);

        var open = InspectorPermissions.All
            .Where(p => InspectorPermissions.IsGranted(p, role, overrides))
            .Select(p => char.ToLowerInvariant(p.Label[0]) + p.Label[1..])
            .ToList();

        var granted = open.Count == 0
            ? "В матрице доступа роли ничего не открыто."
            : "Открыто: " + string.Join(", ", open) + ".";

        // Какие именно документы видит роль, матрицей не настраивается (§2.1 ТЗ СКИД).
        var scope = role switch
        {
            UserRole.Administrator or UserRole.Manager => "Документы — все в пределах допуска.",
            UserRole.Inspector => "Документы — только те, где он назначен инспектором.",
            UserRole.Performer => "Документы — только те, где у него есть поручение.",
            _ => string.Empty,
        };

        return $"{granted} {scope}".TrimEnd();
    }
}

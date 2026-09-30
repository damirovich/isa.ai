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
/// профиль — сам ключ и то, что учётная запись действующая. Правило снятия последнего Администратора у профиля
/// «ИнспекторAI» прежнее: окно первичной настройки самовосстанавливается (<see cref="AdministrationRule"/>).
/// </remarks>
public sealed class InspectorUserRoleCatalog(IUserRoleStore roles) : IUserRoleCatalog
{
    /// <inheritdoc />
    public Task<IReadOnlyList<RoleOption>> ListRolesAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<RoleOption> options = Enum.GetValues<UserRole>()
            .Select(role => new RoleOption(role.ToString(), role.Label(), Describe(role)))
            .ToList();

        return Task.FromResult(options);
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

        await roles.SetRoleAsync(userId, role, cancellationToken);
        return RoleAssignmentResult.Ok;
    }

    /// <summary>Что даёт роль — по построчному доступу к документам (§2.1 ТЗ СКИД).</summary>
    public static string Describe(UserRole role) => role switch
    {
        UserRole.Administrator => "Пользователи, справочники и журнал действий; видит все документы.",
        UserRole.Manager => "Видит все документы системы; единственный, кто снимает документ с контроля.",
        UserRole.Inspector => "Только документы, где сам назначен инспектором, включая отчёты и дашборд.",
        UserRole.Performer => "Только документы, где у него есть хотя бы одно поручение.",
        _ => role.Label(),
    };
}

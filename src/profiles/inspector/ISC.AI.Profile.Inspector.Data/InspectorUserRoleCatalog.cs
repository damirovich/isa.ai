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
/// Назначение ролей здесь НЕ реализуется намеренно: это страница профиля «Роли пользователей»
/// (состав ролей у каждого эксплуатанта свой), пакет роли только показывает.
/// </remarks>
public sealed class InspectorUserRoleCatalog(IUserRoleStore roles) : IUserRoleCatalog
{
    /// <inheritdoc />
    public Task<IReadOnlyList<RoleOption>> ListRolesAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<RoleOption> options = Enum.GetValues<UserRole>()
            .Select(role => new RoleOption(role.ToString(), role.Label()))
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
}

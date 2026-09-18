namespace ISC.AI.Modules.Admin.Domain.Services;

/// <summary>Роль профиля в виде, понятном пакету: непрозрачный ключ и подпись для интерфейса.</summary>
/// <param name="Key">Стабильный ключ (пакет им только фильтрует и сравнивает, смысла не знает).</param>
/// <param name="Label">Подпись на языке эксплуатанта.</param>
public sealed record RoleOption(string Key, string Label);

/// <summary>
/// ПОРТ ПРОФИЛЯ: перечень ролей и назначения по пользователям — только для ОТОБРАЖЕНИЯ на экране
/// учётных записей (колонка «Роль» и фильтр по роли). Пакет роли не назначает и не толкует: назначение
/// — страница профиля, потому что состав ролей у каждого эксплуатанта свой.
/// </summary>
/// <remarks>
/// Ключ намеренно строковый и непрозрачный: перечисление ролей живёт в профиле, и втащить его тип
/// в пакет означало бы зависимость пакета от профиля — запрещено правилом зависимостей (ТС-009).
/// </remarks>
public interface IUserRoleCatalog
{
    /// <summary>Роли профиля для фильтра, в порядке отображения.</summary>
    Task<IReadOnlyList<RoleOption>> ListRolesAsync(CancellationToken cancellationToken = default);

    /// <summary>Назначенные роли: пользователь ядра → ключ роли. Пользователи без роли в словарь не входят.</summary>
    Task<IReadOnlyDictionary<int, string>> GetUserRoleKeysAsync(CancellationToken cancellationToken = default);
}

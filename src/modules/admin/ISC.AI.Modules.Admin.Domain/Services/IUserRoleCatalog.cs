namespace ISC.AI.Modules.Admin.Domain.Services;

/// <summary>Роль профиля в виде, понятном пакету: непрозрачный ключ, подпись и описание для интерфейса.</summary>
/// <param name="Key">Стабильный ключ (пакет им только фильтрует и сравнивает, смысла не знает).</param>
/// <param name="Label">Подпись на языке эксплуатанта.</param>
/// <param name="Description">
/// Что роль может — одной-двумя фразами для карточки сотрудника, чтобы Администратор выбирал роль по смыслу,
/// а не по названию; <see langword="null"/> — описания нет.
/// </param>
public sealed record RoleOption(string Key, string Label, string? Description = null);

/// <summary>Итог назначения роли: успех либо понятная человеку причина отказа.</summary>
/// <param name="Succeeded">Роль назначена (снята).</param>
/// <param name="Error">Причина отказа на языке эксплуатанта; при успехе — <see langword="null"/>.</param>
public sealed record RoleAssignmentResult(bool Succeeded, string? Error = null)
{
    /// <summary>Успех.</summary>
    public static RoleAssignmentResult Ok { get; } = new(true);

    /// <summary>Отказ с причиной.</summary>
    public static RoleAssignmentResult Fail(string error) => new(false, error);
}

/// <summary>
/// ПОРТ ПРОФИЛЯ: роли профиля — перечень с описаниями, назначения по пользователям и само назначение.
/// Экран «Пользователи» пакета показывает роль в списке и меняет её в карточке сотрудника, но правил
/// назначения не знает: какие роли бывают и что запрещено (например, снять последнего Администратора)
/// решает профиль, потому что модель ролей у каждого эксплуатанта своя.
/// </summary>
/// <remarks>
/// Ключ намеренно строковый и непрозрачный: перечисление ролей живёт в профиле, и втащить его тип
/// в пакет означало бы зависимость пакета от профиля — запрещено правилом зависимостей (ТС-009).
/// Право вызывающего назначать роли проверяет сценарий пакета (<see cref="IPlatformAdministration.CanManageAsync"/>)
/// до обращения к порту; порт отвечает за инварианты самой модели ролей (ТП-004/007).
/// </remarks>
public interface IUserRoleCatalog
{
    /// <summary>Роли профиля для фильтра, в порядке отображения.</summary>
    Task<IReadOnlyList<RoleOption>> ListRolesAsync(CancellationToken cancellationToken = default);

    /// <summary>Назначенные роли: пользователь ядра → ключ роли. Пользователи без роли в словарь не входят.</summary>
    Task<IReadOnlyDictionary<int, string>> GetUserRoleKeysAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Назначить пользователю роль (у пользователя одна роль, ТП-007) либо снять её
    /// (<paramref name="roleKey"/> = <see langword="null"/>).
    /// </summary>
    /// <remarks>
    /// Отказ — с причиной для человека: неизвестный ключ, пользователь не найден или отключён, нарушение
    /// инварианта профиля (например, «Следствие» не даёт снять последнего Администратора).
    /// </remarks>
    Task<RoleAssignmentResult> AssignAsync(int userId, string? roleKey, CancellationToken cancellationToken = default);
}

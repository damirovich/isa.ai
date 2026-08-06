using ISC.AI.Profile.Inspector.Domain.Enums;

namespace ISC.AI.Profile.Inspector.Domain.Services;

/// <summary>Пользователь (из <c>core.app_user</c>) с его текущей ролью (<see langword="null"/> — не назначена).</summary>
public sealed record UserRoleRow(int UserId, string DisplayName, UserRole? Role);

/// <summary>
/// Порт ведения ролей пользователей (ТЗ СКИД §2.1, этап 6 Э4-35) — построчный доступ к докфлоу-
/// документам поверх <c>IAccessPolicy</c> (ADR-0014). Список пользователей читает <c>core.app_user</c>
/// напрямую (ядровая таблица, не докфлоу-специфика); роль хранит сам профиль в своей схеме.
/// </summary>
public interface IUserRoleStore
{
    /// <summary>Роль пользователя; <see langword="null"/> — не назначена (сущность не найдена/удалена).</summary>
    Task<UserRole?> GetRoleAsync(int userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Есть ли в системе хотя бы один Администратор. Основа режима ПЕРВИЧНОЙ НАСТРОЙКИ: пока
    /// Администратора нет, управление ролями открыто любому вошедшему — иначе назначить первого
    /// Администратора некому (страница требует Администратора), и система запирается насмерть.
    /// </summary>
    /// <remarks>
    /// Условие именно «нет АДМИНИСТРАТОРА», а не «реестр ролей пуст»: первая версия проверяла пустоту,
    /// и любая первая роль (например «Руководитель», который единственный видит документы) закрывала
    /// окно, оставляя систему без администратора и без способа его назначить — необратимый кирпич.
    /// Правило самовосстанавливающееся: разжаловали последнего Администратора — окно открылось снова.
    /// </remarks>
    Task<bool> AnyAdministratorAsync(CancellationToken cancellationToken = default);

    /// <summary>Все активные пользователи с их текущей ролью, по отображаемому имени.</summary>
    Task<IReadOnlyList<UserRoleRow>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Назначает роль; <paramref name="role"/> = <see langword="null"/> — снимает назначение.</summary>
    Task SetRoleAsync(int userId, UserRole? role, CancellationToken cancellationToken = default);
}

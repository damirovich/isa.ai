namespace ISC.AI.Abstractions.Security;

/// <summary>Строка списка учётных записей (экран администрирования).</summary>
/// <param name="HasLocalPassword">
/// Заведён локальный пароль. <see langword="false"/> — войти по этой учётке нельзя: она либо ещё не
/// перенесена из внешней системы, либо создана без пароля.
/// </param>
/// <param name="MustChangePassword">Пароль временный: выдан администратором и ему известен.</param>
public sealed record UserAccountRow(
    int UserId,
    string UserName,
    string? DisplayName,
    bool IsActive,
    bool HasLocalPassword,
    bool MustChangePassword);

/// <summary>Итог смены собственного пароля.</summary>
public enum PasswordChangeStatus
{
    /// <summary>Пароль изменён.</summary>
    Ok,

    /// <summary>Учётной записи нет либо она отключена.</summary>
    NotFound,

    /// <summary>Текущий пароль указан неверно.</summary>
    WrongCurrentPassword,

    /// <summary>У учётной записи нет локального пароля — менять нечего (вход ещё внешний).</summary>
    NoLocalPassword,

    /// <summary>Новый пароль совпадает с текущим — смена бессмысленна.</summary>
    SameAsCurrent,
}

/// <summary>
/// Ведение учётных записей (<c>core.app_user</c>, Э4-35 §6.5): создание, сброс пароля, включение и
/// отключение, смена собственного пароля.
/// </summary>
/// <remarks>
/// ИНВАРИАНТЫ (ТБ-043, ТБ-016). (1) Пароль НИКОГДА не хранится и не возвращается в открытом виде:
/// временный пароль порождает вызывающий и показывает администратору ОДИН раз, в базу уходит только
/// хеш. (2) Любая смена пароля и любое отключение учётки МЕНЯЮТ штамп безопасности — живые сессии
/// этого пользователя завершаются ревалидацией; без ротации отобранный доступ продолжал бы работать
/// до истечения cookie. (3) Порт профиле-нейтрален: кто вправе администрировать — решает профиль.
/// </remarks>
public interface IUserAccountStore
{
    /// <summary>Все учётные записи (включая отключённые), по отображаемому имени.</summary>
    Task<IReadOnlyList<UserAccountRow>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Создаёт учётную запись с ВРЕМЕННЫМ паролем (его показывает администратору вызывающий).
    /// Возвращает идентификатор либо <see langword="null"/>, если имя входа уже занято.
    /// </summary>
    Task<int?> CreateAsync(
        string userName, string? displayName, string temporaryPassword,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Сбрасывает пароль на временный: помечает учётку как требующую смены и меняет штамп —
    /// живые сессии владельца обрываются немедленно (ТБ-016).
    /// </summary>
    Task<bool> ResetPasswordAsync(
        int userId, string temporaryPassword, CancellationToken cancellationToken = default);

    /// <summary>
    /// Включает или отключает учётную запись. При отключении штамп меняется — сессии обрываются;
    /// без этого отключённый пользователь работал бы до истечения cookie.
    /// </summary>
    Task<bool> SetActiveAsync(int userId, bool isActive, CancellationToken cancellationToken = default);

    /// <summary>
    /// Смена СОБСТВЕННОГО пароля: обязательна проверка текущего (владение сессией не заменяет знание
    /// пароля — иначе перехваченная сессия позволила бы сменить пароль и закрепиться).
    /// </summary>
    Task<PasswordChangeStatus> ChangeOwnPasswordAsync(
        int userId, string currentPassword, string newPassword, CancellationToken cancellationToken = default);
}
